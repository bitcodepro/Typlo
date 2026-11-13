mod adapters;
pub mod jobs;
pub mod received;
mod transfer;
mod trust;

use rustls::{ClientConnection, ServerConfig, ServerConnection, StreamOwned};
use std::{
    io::{self, Read, Write},
    net::{Ipv4Addr, SocketAddr, SocketAddrV4, TcpListener, TcpStream, UdpSocket},
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use trust::{DNS_NAME, Identity, Peer, fingerprint};
pub use trust::{invite, pair, paired_address, set_address, unpair};

pub const PORT: u16 = 49237;
const QUERY: &[u8; 8] = b"TYPCF001";
const ANSWER: &[u8; 8] = b"TYPCF002";
const DISCOVERY_PACKET: usize = 88;
const HANDSHAKE: Duration = Duration::from_secs(3);
const TRANSFER: Duration = Duration::from_secs(180);
const IDLE: Duration = Duration::from_secs(5);
const LISTEN_POLL: Duration = Duration::from_millis(150);

pub(super) struct Listener {
    peer: Peer,
    stop: Arc<AtomicBool>,
    handle: JoinHandle<()>,
}

impl Listener {
    pub fn refresh(slot: &mut Option<Self>, profile: &Path) -> io::Result<()> {
        let desired = Peer::load(profile);
        if let (Ok(Some(peer)), Some(existing)) = (&desired, slot.as_ref())
            && existing.peer.certificate == peer.certificate
            && !existing.handle.is_finished()
        {
            return Ok(());
        }
        *slot = None;
        let Some(peer) = desired? else { return Ok(()) };
        let identity = Identity::load(profile)?;
        let server = identity.server(&peer)?;
        let identity = fingerprint(&identity.certificate);
        let stop = Arc::new(AtomicBool::new(false));
        let handle = {
            let profile = profile.to_owned();
            let peer = peer.clone();
            let stop = stop.clone();
            thread::Builder::new()
                .name("typlo-lan-clipboard".into())
                .spawn(move || {
                    if let Err(error) = listen(&profile, &peer, server, identity, stop) {
                        eprintln!("Typlo LAN clipboard: {error}");
                    }
                })?
        };
        *slot = Some(Self { peer, stop, handle });
        Ok(())
    }
}

impl Drop for Listener {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        self.handle.thread().unpark();
    }
}

fn listen(
    profile: &Path,
    peer: &Peer,
    server: Arc<ServerConfig>,
    identity: [u8; 32],
    stop: Arc<AtomicBool>,
) -> io::Result<()> {
    let bind = SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, PORT);
    // The previous listener may still be closing after a change in Settings.
    let mut retries = 0;
    let listener = loop {
        match TcpListener::bind(bind) {
            Ok(listener) => break listener,
            Err(error) if error.kind() == io::ErrorKind::AddrInUse && retries < 10 => {
                if stop.load(Ordering::Acquire) {
                    return Ok(());
                }
                retries += 1;
                thread::park_timeout(LISTEN_POLL);
            }
            Err(error) => return Err(error),
        }
    };
    listener.set_nonblocking(true)?;
    // A manually configured address still works if UDP discovery is unavailable.
    let discovery = UdpSocket::bind(bind).and_then(|socket| {
        socket.set_nonblocking(true)?;
        Ok(socket)
    });
    let peer_fingerprint = fingerprint(&peer.certificate);
    let mut active: Option<JoinHandle<()>> = None;
    while !stop.load(Ordering::Acquire) {
        if let Ok(discovery) = &discovery {
            respond_to_discovery(discovery, &identity, &peer_fingerprint);
        }
        if active.as_ref().is_some_and(JoinHandle::is_finished) {
            let _ = active.take().expect("finished worker").join();
        }
        if let Ok((socket, SocketAddr::V4(address))) = listener.accept()
            && local_address(*address.ip())
            && active.is_none()
        {
            let profile = profile.to_owned();
            let peer = peer.clone();
            let server = server.clone();
            let stop = stop.clone();
            active = thread::Builder::new()
                .name("typlo-lan-receive".into())
                .spawn(move || {
                    let _ = receive(&profile, &peer, server, socket, stop);
                })
                .ok();
        }
        thread::park_timeout(LISTEN_POLL);
    }
    Ok(())
}

fn respond_to_discovery(socket: &UdpSocket, identity: &[u8; 32], peer: &[u8; 32]) {
    let mut message = [0; DISCOVERY_PACKET + 1];
    for _ in 0..16 {
        let Ok((length, SocketAddr::V4(address))) = socket.recv_from(&mut message) else {
            break;
        };
        if length == DISCOVERY_PACKET
            && local_address(*address.ip())
            && &message[..8] == QUERY
            && &message[24..56] == identity
            && &message[56..88] == peer
        {
            message[..8].copy_from_slice(ANSWER);
            let _ = socket.send_to(&message[..DISCOVERY_PACKET], address);
        }
    }
}

struct Discovery {
    addresses: Vec<SocketAddrV4>,
    probes: usize,
}

fn discover(identity: &Identity, peer: &Peer) -> io::Result<Discovery> {
    let mut message = [0; DISCOVERY_PACKET];
    message[..8].copy_from_slice(QUERY);
    let rng = rustls::crypto::ring::default_provider();
    rng.secure_random
        .fill(&mut message[8..24])
        .map_err(|_| io::Error::other("Could not generate a discovery nonce"))?;
    message[24..56].copy_from_slice(&fingerprint(&peer.certificate));
    message[56..88].copy_from_slice(&fingerprint(&identity.certificate));
    let adapters = adapters::broadcasts();
    let mut sockets = Vec::new();
    let mut last_error = None;
    for (source, destination) in adapters
        .as_deref()
        .unwrap_or_default()
        .iter()
        .copied()
        .chain([(Ipv4Addr::UNSPECIFIED, Ipv4Addr::BROADCAST)])
    {
        match UdpSocket::bind((source, 0)).and_then(|socket| {
            socket.set_broadcast(true)?;
            socket.set_nonblocking(true)?;
            socket.send_to(&message, (destination, PORT))?;
            Ok(socket)
        }) {
            Ok(socket) => sockets.push(socket),
            Err(error) => last_error = Some(error),
        }
    }
    if sockets.is_empty() {
        return Err(last_error
            .or_else(|| adapters.err())
            .unwrap_or_else(|| invalid("No usable LAN interface")));
    }
    message[..8].copy_from_slice(ANSWER);
    let mut buffer = [0; DISCOVERY_PACKET + 1];
    let mut end = Instant::now() + Duration::from_millis(850);
    let mut found = Vec::new();
    while found.len() < 4 {
        if Instant::now() >= end {
            break;
        }
        for socket in &sockets {
            for _ in 0..8 {
                match socket.recv_from(&mut buffer) {
                    Ok((DISCOVERY_PACKET, SocketAddr::V4(address)))
                        if address.port() == PORT
                            && local_address(*address.ip())
                            && buffer[..DISCOVERY_PACKET] == message =>
                    {
                        if found.len() < 4 && !found.contains(&address) {
                            if found.is_empty() {
                                end = end.min(Instant::now() + Duration::from_millis(35));
                            }
                            found.push(address);
                        }
                    }
                    Ok(_) => {}
                    Err(_) => break,
                }
            }
        }
        thread::sleep(Duration::from_millis(10));
    }
    Ok(Discovery {
        addresses: found,
        probes: sockets.len(),
    })
}

fn send(
    profile: &Path,
    id: i64,
    expected_peer: &[u8; 32],
    transfer_id: &[u8; 16],
    stop: Arc<AtomicBool>,
    mut progress: impl FnMut(u64, u64) -> io::Result<()>,
) -> io::Result<()> {
    let peer =
        Peer::load(profile)?.ok_or_else(|| invalid("Pair the other computer in Settings"))?;
    if &fingerprint(&peer.certificate) != expected_peer {
        return Err(invalid("The paired computer changed during transfer"));
    }
    let identity = Identity::load(profile)?;
    let client = identity.client(&peer)?;
    check_cancelled(&stop)?;
    let payload = transfer::Payload::prepare(profile, id);
    let send = |mut stream: StreamOwned<ClientConnection, Socket>| {
        if !is_current_peer(profile, &peer)? {
            return Err(invalid("The paired computer changed during transfer"));
        }
        stream.sock.stop = Some(stop.clone());
        send_payload(&mut stream, transfer_id, || payload, &mut progress)
    };
    let manual = peer.address.map(|ip| SocketAddrV4::new(ip, PORT));
    let mut last_error = None;
    if let Some(address) = manual {
        check_cancelled(&stop)?;
        match connect(address.into(), &peer, client.clone()) {
            Ok(stream) => return send(stream),
            Err(error) => last_error = Some(error),
        }
    }
    let discovery = discover(&identity, &peer)?;
    for address in discovery
        .addresses
        .into_iter()
        .filter(|address| Some(*address) != manual)
    {
        check_cancelled(&stop)?;
        match connect(address.into(), &peer, client.clone()) {
            Ok(stream) => return send(stream),
            Err(error) => last_error = Some(error),
        }
    }
    Err(last_error.unwrap_or_else(|| io::Error::new(
        io::ErrorKind::NotConnected,
        "The paired Typlo did not answer discovery. Use Check connection in Settings to see the network status.",
    )))
}

fn check_cancelled(stop: &AtomicBool) -> io::Result<()> {
    if stop.load(Ordering::Acquire) {
        return Err(io::Error::new(
            io::ErrorKind::ConnectionAborted,
            "Transfer cancelled",
        ));
    }
    Ok(())
}

pub fn check_connection(profile: &Path) -> io::Result<String> {
    let peer =
        Peer::load(profile)?.ok_or_else(|| invalid("Pair the other computer in Settings"))?;
    let identity = Identity::load(profile)?;
    let client = identity.client(&peer)?;
    let (mut report, mut addresses) = match discover(&identity, &peer) {
        Ok(result) if result.addresses.is_empty() => (
            format!(
                "Discovery: no response after {} broadcast probes",
                result.probes
            ),
            Vec::new(),
        ),
        Ok(result) => (
            format!(
                "Discovery: {}",
                result
                    .addresses
                    .iter()
                    .map(|address| address.ip().to_string())
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            result.addresses,
        ),
        Err(error) => (format!("Discovery: {error}"), Vec::new()),
    };
    if let Some(address) = peer.address.map(|ip| SocketAddrV4::new(ip, PORT))
        && !addresses.contains(&address)
    {
        addresses.push(address);
    }
    if addresses.is_empty() {
        report.push_str("\nTCP and certificate: no address to check");
    }
    for address in addresses {
        match connect(address.into(), &peer, client.clone()) {
            Ok(mut stream) => {
                use std::fmt::Write as _;
                let _ = write!(
                    report,
                    "\nTCP: connected to {}\nCertificate: matched",
                    address.ip()
                );
                let ping = stream
                    .write_all(transfer::PING)
                    .and_then(|()| stream.flush())
                    .and_then(|()| read_reply(&mut stream));
                let _ = match ping {
                    Ok(()) => write!(report, "\nReceiver: ready"),
                    Err(error) => write!(report, "\nReceiver: {error}"),
                };
                break;
            }
            Err(error) => {
                use std::fmt::Write as _;
                let _ = write!(report, "\n{}: {error}", address.ip());
            }
        }
    }
    Ok(report)
}

fn connect(
    address: SocketAddr,
    peer: &Peer,
    client: Arc<rustls::ClientConfig>,
) -> io::Result<StreamOwned<ClientConnection, Socket>> {
    let tcp = TcpStream::connect_timeout(&address, Duration::from_millis(900))
        .map_err(|error| io::Error::new(error.kind(), format!("TCP connection failed: {error}")))?;
    let mut socket = Socket::new(tcp, None)?;
    let mut connection =
        ClientConnection::new(client, DNS_NAME.try_into().expect("valid TLS server name"))
            .map_err(io::Error::other)?;
    connection.complete_io(&mut socket).map_err(|error| {
        io::Error::new(error.kind(), format!("TLS authentication failed: {error}"))
    })?;
    if !matches_peer(connection.peer_certificates(), peer) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "TLS certificate does not match the paired computer",
        ));
    }
    socket.extend_deadline();
    Ok(StreamOwned::new(connection, socket))
}

fn is_current_peer(profile: &Path, peer: &Peer) -> io::Result<bool> {
    Ok(Peer::load(profile)?.is_some_and(|saved| saved.certificate == peer.certificate))
}

fn send_payload(
    stream: &mut (impl Read + Write),
    id: &[u8; 16],
    payload: impl FnOnce() -> io::Result<transfer::Payload>,
    progress: impl FnMut(u64, u64) -> io::Result<()>,
) -> io::Result<()> {
    stream.write_all(transfer::RETRY_VERSION)?;
    stream.write_all(id)?;
    stream.flush()?;
    let mut reply = [0];
    stream.read_exact(&mut reply)?;
    match reply[0] {
        4 => return Ok(()),
        0 => {}
        _ => {
            return Err(invalid(
                "Receiver rejected the transfer request. Check that both computers use Preview 27 or newer.",
            ));
        }
    }
    payload()?.send_body(stream, progress)?;
    read_reply(stream)
}

fn read_reply(stream: &mut impl Read) -> io::Result<()> {
    let mut reply = [0];
    stream.read_exact(&mut reply)?;
    match reply[0] {
        0 => Ok(()),
        1 => Err(invalid("The other computer could not save the entry")),
        2 => Err(invalid(
            "The entry exceeds the other computer's clipboard history limits",
        )),
        3 => Err(invalid(
            "Clipboard history is disabled on the other computer",
        )),
        _ => Err(invalid("Invalid reply from the other computer")),
    }
}

fn receive(
    profile: &Path,
    peer: &Peer,
    server: Arc<ServerConfig>,
    socket: TcpStream,
    stop: Arc<AtomicBool>,
) -> io::Result<()> {
    socket.set_nonblocking(false)?;
    let mut socket = Socket::new(socket, Some(stop.clone()))?;
    let mut connection = ServerConnection::new(server).map_err(io::Error::other)?;
    connection.complete_io(&mut socket)?;
    if !matches_peer(connection.peer_certificates(), peer) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Unknown Typlo device",
        ));
    }
    socket.extend_deadline();
    let mut stream = StreamOwned::new(connection, socket);
    let result = transfer::receive(
        profile,
        &mut stream,
        &fingerprint(&peer.certificate),
        || {
            if stop.load(Ordering::Acquire) || !is_current_peer(profile, peer)? {
                return Err(invalid("The paired computer changed during transfer"));
            }
            Ok(())
        },
    );
    if matches!(result, Ok(false)) {
        return Ok(());
    }
    let code = match result.as_ref().err().map(io::Error::kind) {
        None => 0,
        Some(io::ErrorKind::FileTooLarge) => 2,
        Some(io::ErrorKind::PermissionDenied) => 3,
        Some(_) => 1,
    };
    stream.write_all(&[code])?;
    stream.flush()?;
    result.map(|_| ())
}

fn matches_peer(
    certificates: Option<&[rustls::pki_types::CertificateDer<'_>]>,
    peer: &Peer,
) -> bool {
    certificates
        .and_then(|certificates| certificates.first())
        .is_some_and(|certificate| certificate.as_ref() == peer.certificate)
}

struct Socket {
    inner: TcpStream,
    deadline: Instant,
    stop: Option<Arc<AtomicBool>>,
}

impl Socket {
    fn new(inner: TcpStream, stop: Option<Arc<AtomicBool>>) -> io::Result<Self> {
        inner.set_nodelay(true)?;
        Ok(Self {
            inner,
            deadline: Instant::now() + HANDSHAKE,
            stop,
        })
    }

    fn extend_deadline(&mut self) {
        self.deadline = Instant::now() + TRANSFER;
    }

    fn timeout(&self) -> io::Result<Option<Duration>> {
        if self
            .stop
            .as_ref()
            .is_some_and(|stop| stop.load(Ordering::Acquire))
        {
            return Err(io::Error::other("Typlo clipboard sharing stopped"));
        }
        let remaining = self.deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "Typlo clipboard connection timed out",
            ));
        }
        Ok(Some(remaining.min(IDLE)))
    }
}

impl Read for Socket {
    fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
        self.inner.set_read_timeout(self.timeout()?)?;
        self.inner.read(bytes)
    }
}

impl Write for Socket {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.inner.set_write_timeout(self.timeout()?)?;
        self.inner.write(bytes)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.timeout()?;
        self.inner.flush()
    }
}

pub fn received_folder(profile: &Path) -> PathBuf {
    profile.join("Received files")
}

pub fn open_received_folder(profile: &Path) -> io::Result<()> {
    let path = received_folder(profile);
    std::fs::create_dir_all(&path)?;
    std::process::Command::new("explorer.exe")
        .arg(path)
        .spawn()?;
    Ok(())
}

pub fn received_bytes(profile: &Path) -> io::Result<u64> {
    folder_bytes(received_folder(profile))
}

fn folder_bytes(folder: PathBuf) -> io::Result<u64> {
    use std::{fs, os::windows::fs::MetadataExt};
    let mut pending = vec![folder];
    let mut total = 0u64;
    while let Some(folder) = pending.pop() {
        let entries = match fs::read_dir(folder) {
            Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
            result => result?,
        };
        for entry in entries {
            let entry = entry?;
            let metadata = entry.metadata()?;
            if metadata.file_attributes() & 0x400 != 0 {
                continue;
            }
            if metadata.is_dir() {
                pending.push(entry.path());
            } else if metadata.is_file() {
                total = total.saturating_add(metadata.len());
            }
        }
    }
    Ok(total)
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn local_address(ip: Ipv4Addr) -> bool {
    ip.is_private() || ip.is_link_local() || (cfg!(test) && ip.is_loopback())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::windows::{
        clipio::{Clip, Kind},
        history::{History, Limits},
    };
    use std::fs;

    struct Paired {
        sender: tempfile::TempDir,
        receiver: tempfile::TempDir,
    }

    impl Paired {
        fn new() -> Self {
            let sender = tempfile::tempdir().unwrap();
            let receiver = tempfile::tempdir().unwrap();
            let left = invite(sender.path()).unwrap();
            let right = invite(receiver.path()).unwrap();
            assert!(pair(sender.path(), &left, "").is_err());
            pair(sender.path(), &right, "").unwrap();
            pair(receiver.path(), &left, "").unwrap();
            Self { sender, receiver }
        }

        fn send(&self, clip: &Clip, sender: &Identity) -> (io::Result<()>, io::Result<()>) {
            let mut history = History::open(self.sender.path()).unwrap();
            history
                .store(
                    clip,
                    Limits {
                        max_items: 10,
                        max_total: 1024 * 1024,
                        max_image: 1024 * 1024,
                        max_text: 1024 * 1024,
                    },
                )
                .unwrap();
            let id = history.list(0).unwrap()[0].id;
            let payload = transfer::Payload::prepare(self.sender.path(), id).unwrap();
            self.exchange(sender, |mut stream| {
                let mut id = [0; 16];
                rustls::crypto::ring::default_provider()
                    .secure_random
                    .fill(&mut id)
                    .unwrap();
                send_payload(&mut stream, &id, || Ok(payload), |_, _| Ok(()))
            })
        }

        fn exchange(
            &self,
            sender: &Identity,
            exchange: impl FnOnce(StreamOwned<ClientConnection, Socket>) -> io::Result<()>,
        ) -> (io::Result<()>, io::Result<()>) {
            let peer = Peer::load(self.sender.path()).unwrap().unwrap();
            let receiver_peer = Peer::load(self.receiver.path()).unwrap().unwrap();
            let server = Identity::load(self.receiver.path())
                .unwrap()
                .server(&receiver_peer)
                .unwrap();
            let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
            listener.set_nonblocking(true).unwrap();
            let address = listener.local_addr().unwrap();
            thread::scope(|scope| {
                let incoming = scope.spawn(|| {
                    let until = Instant::now() + HANDSHAKE;
                    loop {
                        match listener.accept() {
                            Ok((socket, _)) => {
                                break receive(
                                    self.receiver.path(),
                                    &receiver_peer,
                                    server,
                                    socket,
                                    Arc::default(),
                                );
                            }
                            Err(error)
                                if error.kind() == io::ErrorKind::WouldBlock
                                    && Instant::now() < until =>
                            {
                                thread::park_timeout(Duration::from_millis(10));
                            }
                            Err(error) => break Err(error),
                        }
                    }
                });
                let outgoing =
                    connect(address, &peer, sender.client(&peer).unwrap()).and_then(exchange);
                (outgoing, incoming.join().unwrap())
            })
        }

        fn receive_clip(&self, kind: Kind) -> Clip {
            let history = History::open(self.receiver.path()).unwrap();
            let item = history
                .list(0)
                .unwrap()
                .into_iter()
                .find(|item| item.kind == kind)
                .unwrap();
            assert_eq!(item.source.as_deref(), Some("Typlo LAN"));
            history.load(item.id).unwrap().unwrap()
        }
    }

    #[test]
    #[ignore = "Run explicitly for clipboard, screenshot and LAN endurance testing"]
    fn paired_history_and_screenshot_soak() {
        let seconds = std::env::var("TYPLO_SOAK_SECONDS")
            .ok()
            .and_then(|value| value.parse::<u64>().ok())
            .unwrap_or(120)
            .clamp(5, 28_800);
        let trial = Paired::new();
        let identity = Identity::load(trial.sender.path()).unwrap();
        let file = trial.sender.path().join("soak.bin");
        let mut pixels = 0x9e37_79b9u32;
        let image = image::RgbaImage::from_fn(384, 256, |_, _| {
            pixels ^= pixels << 13;
            pixels ^= pixels >> 17;
            pixels ^= pixels << 5;
            let [r, g, b, _] = pixels.to_le_bytes();
            image::Rgba([r, g, b, 255])
        });
        let started = Instant::now();
        let mut cycles = 0;
        while started.elapsed() < Duration::from_secs(seconds) {
            let (clip, kind) = match cycles % 3 {
                0 => (
                    Clip::from_text(&format!("Typlo soak {cycles} 🙂")),
                    Kind::Text,
                ),
                1 => {
                    crate::capture::remember(&image, trial.sender.path()).unwrap();
                    assert_eq!(
                        crate::capture::load_last(trial.sender.path()).unwrap(),
                        image
                    );
                    crate::capture::write_jpeg(&image, &trial.sender.path().join("soak.jpg"))
                        .unwrap();
                    let mut png = io::Cursor::new(Vec::new());
                    image.write_to(&mut png, image::ImageFormat::Png).unwrap();
                    (
                        Clip::from_legacy(2, png.into_inner(), None).unwrap(),
                        Kind::Image,
                    )
                }
                _ => {
                    fs::write(&file, vec![(cycles % 251) as u8; 128 * 1024]).unwrap();
                    (
                        Clip::from_legacy(3, file.to_str().unwrap().as_bytes().to_vec(), None)
                            .unwrap(),
                        Kind::Files,
                    )
                }
            };
            let (sent, received) = trial.send(&clip, &identity);
            sent.unwrap();
            received.unwrap();
            let history = History::open(trial.receiver.path()).unwrap();
            let item = history.list(0).unwrap().remove(0);
            assert_eq!(item.kind, kind);
            if kind == Kind::Files {
                assert_eq!(
                    received::bytes(trial.receiver.path(), item.id).unwrap(),
                    128 * 1024
                );
                received::delete(trial.receiver.path(), item.id).unwrap();
            } else {
                assert_eq!(
                    history.load(item.id).unwrap().unwrap().identity(),
                    clip.identity()
                );
            }
            cycles += 1;
        }
        println!(
            "confirmed {cycles} LAN transfers with history and screenshot work in {:.1}s",
            started.elapsed().as_secs_f64()
        );
        assert!(cycles >= 3, "each payload type should be exercised");
    }

    #[test]
    fn retry_after_unread_confirmation_keeps_one_file_and_can_remove_it_safely() {
        let pair = Paired::new();
        let identity = Identity::load(pair.sender.path()).unwrap();
        let original = pair.sender.path().join("report.txt");
        fs::write(&original, b"one report").unwrap();
        let clip =
            Clip::from_legacy(3, original.to_str().unwrap().as_bytes().to_vec(), None).unwrap();
        let mut source = History::open(pair.sender.path()).unwrap();
        source
            .store(
                &clip,
                Limits {
                    max_items: 10,
                    max_total: 1024,
                    max_image: 1024,
                    max_text: 1024,
                },
            )
            .unwrap();
        let entry = source.list(0).unwrap()[0].id;
        let payload = transfer::Payload::prepare(pair.sender.path(), entry).unwrap();
        let transfer = [33; 16];
        let (sent, _) = pair.exchange(&identity, |mut stream| {
            stream.write_all(transfer::RETRY_VERSION)?;
            stream.write_all(&transfer)?;
            stream.flush()?;
            read_reply(&mut stream)?;
            payload.send_body(&mut stream, |_, _| Ok(()))
        });
        sent.unwrap();
        let (retried, accepted) = pair.exchange(&identity, |mut stream| {
            send_payload(
                &mut stream,
                &transfer,
                || Err(io::Error::other("Retry must not reopen the source")),
                |_, _| Ok(()),
            )
        });
        retried.unwrap();
        accepted.unwrap();
        let history = History::open(pair.receiver.path()).unwrap();
        let items = history.list(0).unwrap();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].received_bytes, Some(10));
        let root = received_folder(pair.receiver.path());
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        assert_eq!(
            received::bytes(pair.receiver.path(), items[0].id).unwrap(),
            10
        );
        let unrelated = root.join("unrelated.txt");
        fs::write(&unrelated, b"keep").unwrap();
        received::delete(pair.receiver.path(), items[0].id).unwrap();
        assert!(history.list(0).unwrap().is_empty());
        assert_eq!(fs::read(&unrelated).unwrap(), b"keep");
        assert_eq!(fs::read(&original).unwrap(), b"one report");
    }

    #[test]
    fn paired_profiles_transfer_text_image_and_nested_files_over_tls() {
        let pair = Paired::new();
        let identity = Identity::load(pair.sender.path()).unwrap();
        let text = Clip::from_text("привіт 🙂");
        let (sent, received) = pair.send(&text, &identity);
        sent.unwrap();
        received.unwrap();
        assert_eq!(pair.receive_clip(Kind::Text).text(), text.text());

        let mut png = io::Cursor::new(Vec::new());
        image::RgbaImage::from_pixel(2, 3, image::Rgba([22, 44, 66, 255]))
            .write_to(&mut png, image::ImageFormat::Png)
            .unwrap();
        let mut clip = Clip::from_legacy(2, png.into_inner(), None).unwrap();
        clip.set_editable_text("Image caption 🙂").unwrap();
        let (sent, received) = pair.send(&clip, &identity);
        sent.unwrap();
        received.unwrap();
        let received_image = pair.receive_clip(Kind::Image);
        assert_eq!(received_image.identity(), clip.identity());
        assert_eq!(received_image.editable_text(), "Image caption 🙂");

        let folder = pair.sender.path().join("Файлы");
        let nested = folder.join("Sub");
        fs::create_dir_all(&nested).unwrap();
        fs::write(nested.join("data.bin"), b"contents").unwrap();
        let mut clip =
            Clip::from_legacy(3, folder.to_str().unwrap().as_bytes().to_vec(), None).unwrap();
        clip.set_editable_text("Folder caption\nSecond line")
            .unwrap();
        let (sent, received) = pair.send(&clip, &identity);
        sent.unwrap();
        received.unwrap();
        let clip = pair.receive_clip(Kind::Files);
        assert_eq!(clip.editable_text(), "Folder caption\nSecond line");
        let saved = PathBuf::from(clip.text());
        assert!(saved.starts_with(received_folder(pair.receiver.path())));
        assert_eq!(fs::read(saved.join("Sub/data.bin")).unwrap(), b"contents");
    }

    #[test]
    fn rejects_unpaired_client_and_reports_disabled_history() {
        let pair = Paired::new();
        let text = Clip::from_text("private");
        let (sent, received) = pair.send(&text, &Identity::generate().unwrap());
        assert!(sent.is_err());
        assert!(received.is_err());
        let settings = crate::config::Config {
            history_enabled: false,
            ..Default::default()
        };
        crate::config::save(pair.receiver.path(), &settings).unwrap();
        let (sent, received) = pair.send(&text, &Identity::load(pair.sender.path()).unwrap());
        assert!(
            sent.unwrap_err()
                .to_string()
                .contains("history is disabled")
        );
        assert_eq!(
            received.unwrap_err().kind(),
            io::ErrorKind::PermissionDenied
        );
        assert!(
            History::open(pair.receiver.path())
                .unwrap()
                .list(0)
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn discovery_echoes_only_the_matching_pair_and_nonce() {
        let responder = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        responder
            .set_read_timeout(Some(Duration::from_millis(50)))
            .unwrap();
        let requester = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        requester
            .set_read_timeout(Some(Duration::from_secs(1)))
            .unwrap();
        let address = responder.local_addr().unwrap();
        let mut packet = [0; DISCOVERY_PACKET];
        packet[..8].copy_from_slice(QUERY);
        packet[8..24].fill(42);
        packet[24..56].fill(1);
        requester.send_to(&packet, address).unwrap();
        packet[56..88].fill(2);
        requester.send_to(&packet, address).unwrap();
        respond_to_discovery(&responder, &[1; 32], &[2; 32]);
        let mut reply = [0; DISCOVERY_PACKET];
        assert_eq!(
            requester.recv_from(&mut reply).unwrap(),
            (DISCOVERY_PACKET, address)
        );
        packet[..8].copy_from_slice(ANSWER);
        assert_eq!(reply, packet);
    }
}
