use super::{invalid, local_address};
use crate::windows::history::{protect, unprotect};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use rustls::{
    ClientConfig, RootCertStore, ServerConfig,
    pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer},
    server::WebPkiClientVerifier,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{fs, io, io::Write, net::Ipv4Addr, path::Path, sync::Arc};

const IDENTITY: &str = "lan-clipboard-identity";
const PEER: &str = "lan-clipboard-peer.json";
const PREFIX: &str = "typlo1:";
pub(super) const DNS_NAME: &str = "clipboard.typlo.local";

#[derive(Serialize, Deserialize)]
pub(super) struct Identity {
    pub certificate: Vec<u8>,
    key: Vec<u8>,
}

#[derive(Clone, Serialize, Deserialize, PartialEq, Eq)]
pub(super) struct Peer {
    pub certificate: Vec<u8>,
    pub address: Option<Ipv4Addr>,
}

impl Identity {
    pub fn load(profile: &Path) -> io::Result<Self> {
        serde_json::from_slice(&unprotect(&fs::read(profile.join(IDENTITY))?)?)
            .map_err(io::Error::other)
    }

    pub(super) fn generate() -> io::Result<Self> {
        let mut params =
            rcgen::CertificateParams::new(vec![DNS_NAME.into()]).map_err(io::Error::other)?;
        params.distinguished_name = rcgen::DistinguishedName::new();
        params
            .distinguished_name
            .push(rcgen::DnType::CommonName, DNS_NAME);
        params.extended_key_usages = vec![
            rcgen::ExtendedKeyUsagePurpose::ServerAuth,
            rcgen::ExtendedKeyUsagePurpose::ClientAuth,
        ];
        let key = rcgen::KeyPair::generate().map_err(io::Error::other)?;
        let certificate = params.self_signed(&key).map_err(io::Error::other)?;
        Ok(Self {
            certificate: certificate.der().to_vec(),
            key: key.serialize_der(),
        })
    }

    fn get_or_create(profile: &Path) -> io::Result<Self> {
        match Self::load(profile) {
            Ok(identity) => return Ok(identity),
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }
        fs::create_dir_all(profile)?;
        let identity = Self::generate()?;
        let data = serde_json::to_vec(&identity).map_err(io::Error::other)?;
        let mut file = tempfile::NamedTempFile::new_in(profile)?;
        file.write_all(&protect(&data)?)?;
        file.as_file().sync_all()?;
        match file.persist_noclobber(profile.join(IDENTITY)) {
            Ok(_) => Ok(identity),
            Err(error) if error.error.kind() == io::ErrorKind::AlreadyExists => Self::load(profile),
            Err(error) => Err(error.error),
        }
    }

    fn chain(&self) -> Vec<CertificateDer<'static>> {
        vec![CertificateDer::from(self.certificate.clone())]
    }

    fn private_key(&self) -> PrivateKeyDer<'static> {
        PrivatePkcs8KeyDer::from(self.key.clone()).into()
    }

    pub fn client(&self, peer: &Peer) -> io::Result<Arc<ClientConfig>> {
        let config =
            ClientConfig::builder_with_provider(Arc::new(rustls::crypto::ring::default_provider()))
                .with_protocol_versions(&[&rustls::version::TLS13])
                .map_err(io::Error::other)?
                .with_root_certificates(peer.roots()?)
                .with_client_auth_cert(self.chain(), self.private_key())
                .map_err(io::Error::other)?;
        Ok(Arc::new(config))
    }

    pub fn server(&self, peer: &Peer) -> io::Result<Arc<ServerConfig>> {
        let provider = Arc::new(rustls::crypto::ring::default_provider());
        let verifier =
            WebPkiClientVerifier::builder_with_provider(Arc::new(peer.roots()?), provider.clone())
                .build()
                .map_err(io::Error::other)?;
        let config = ServerConfig::builder_with_provider(provider)
            .with_protocol_versions(&[&rustls::version::TLS13])
            .map_err(io::Error::other)?
            .with_client_cert_verifier(verifier)
            .with_single_cert(self.chain(), self.private_key())
            .map_err(io::Error::other)?;
        Ok(Arc::new(config))
    }
}

impl Peer {
    fn roots(&self) -> io::Result<RootCertStore> {
        let mut roots = RootCertStore::empty();
        roots
            .add(CertificateDer::from(self.certificate.as_slice()))
            .map_err(io::Error::other)?;
        Ok(roots)
    }

    pub fn load(profile: &Path) -> io::Result<Option<Self>> {
        let bytes = match fs::read(profile.join(PEER)) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        let peer: Self = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
        if peer.address.is_some_and(|ip| !local_address(ip)) {
            return Err(invalid("Enter a private IPv4 address"));
        }
        Ok(Some(peer))
    }
}

pub fn invite(profile: &Path) -> io::Result<String> {
    let identity = Identity::get_or_create(profile)?;
    Ok(format!(
        "{PREFIX}{}",
        URL_SAFE_NO_PAD.encode(identity.certificate)
    ))
}

pub fn pair(profile: &Path, code: &str, address: &str) -> io::Result<()> {
    if code.len() > 8192 {
        return Err(invalid("Pairing code is too long"));
    }
    let code: String = code
        .chars()
        .filter(|character| !character.is_ascii_whitespace())
        .collect();
    let code = code
        .strip_prefix(PREFIX)
        .filter(|code| code.len() <= 4096)
        .ok_or_else(|| invalid("Enter the pairing code from the other computer"))?;
    let certificate = URL_SAFE_NO_PAD.decode(code).map_err(io::Error::other)?;
    let address = parse_address(address)?;
    if Identity::get_or_create(profile)?.certificate == certificate {
        return Err(invalid(
            "Enter the other computer's code, not this computer's code",
        ));
    }
    save_peer(
        profile,
        &Peer {
            certificate,
            address,
        },
    )
}

pub fn set_address(profile: &Path, address: &str) -> io::Result<()> {
    let mut peer = Peer::load(profile)?.ok_or_else(|| invalid("Pair the other computer first"))?;
    peer.address = parse_address(address)?;
    save_peer(profile, &peer)
}

fn parse_address(address: &str) -> io::Result<Option<Ipv4Addr>> {
    let address = address.trim();
    if address.is_empty() {
        return Ok(None);
    }
    address
        .parse::<Ipv4Addr>()
        .ok()
        .filter(|ip| local_address(*ip))
        .map(Some)
        .ok_or_else(|| invalid("Enter a private IPv4 address"))
}

fn save_peer(profile: &Path, peer: &Peer) -> io::Result<()> {
    peer.roots()?;
    let mut file = tempfile::NamedTempFile::new_in(profile)?;
    serde_json::to_writer(&mut file, peer).map_err(io::Error::other)?;
    file.as_file().sync_all()?;
    file.persist(profile.join(PEER))
        .map_err(|error| error.error)?;
    Ok(())
}

pub fn unpair(profile: &Path) -> io::Result<()> {
    match fs::remove_file(profile.join(PEER)) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        result => result,
    }
}

pub fn paired_address(profile: &Path) -> io::Result<Option<String>> {
    Ok(Peer::load(profile)?.map(|peer| peer.address.map_or_else(String::new, |ip| ip.to_string())))
}

pub(super) fn fingerprint(certificate: &[u8]) -> [u8; 32] {
    Sha256::digest(certificate).into()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pairing_accepts_line_breaks_in_a_transcribed_certificate() {
        let sender = tempfile::tempdir().unwrap();
        let receiver = tempfile::tempdir().unwrap();
        let code = invite(sender.path()).unwrap();
        let mut wrapped = String::new();
        for (index, character) in code.chars().enumerate() {
            if index > 0 && index % 48 == 0 {
                wrapped.push_str("\r\n ");
            }
            wrapped.push(character);
        }
        pair(receiver.path(), &wrapped, "").unwrap();
        assert_eq!(
            Peer::load(receiver.path()).unwrap().unwrap().certificate,
            Identity::load(sender.path()).unwrap().certificate,
        );
    }
}
