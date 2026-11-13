use super::local_address;
use std::{io, net::Ipv4Addr, ptr};
use windows_sys::Win32::{
    Foundation::{ERROR_BUFFER_OVERFLOW, ERROR_NO_DATA},
    NetworkManagement::{
        IpHelper::{
            GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER, GAA_FLAG_SKIP_MULTICAST,
            GetAdaptersAddresses,
        },
        Ndis::IfOperStatusUp,
    },
    Networking::WinSock::{AF_INET, SOCKADDR_IN},
};

pub(super) fn broadcasts() -> io::Result<Vec<(Ipv4Addr, Ipv4Addr)>> {
    let mut size = 16_384u32;
    loop {
        let mut buffer = vec![0usize; (size as usize).div_ceil(size_of::<usize>())];
        let first = buffer.as_mut_ptr().cast();
        // The aligned buffer owns the linked adapter and address structures
        // until all their IPv4 addresses and prefix lengths have been copied.
        let result = unsafe {
            GetAdaptersAddresses(
                AF_INET.into(),
                GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
                ptr::null(),
                first,
                &mut size,
            )
        };
        match result {
            ERROR_BUFFER_OVERFLOW if size <= 1024 * 1024 => continue,
            ERROR_NO_DATA => return Ok(Vec::new()),
            0 => {}
            code => return Err(io::Error::from_raw_os_error(code as i32)),
        }
        let mut output = Vec::new();
        let mut adapter = first;
        while let Some(current) = unsafe { adapter.as_ref() } {
            adapter = current.Next;
            if current.OperStatus != IfOperStatusUp {
                continue;
            }
            let mut address = current.FirstUnicastAddress;
            while let Some(entry) = unsafe { address.as_ref() } {
                address = entry.Next;
                let socket = entry.Address;
                if socket.lpSockaddr.is_null()
                    || socket.iSockaddrLength < size_of::<SOCKADDR_IN>() as i32
                    || unsafe { (*socket.lpSockaddr).sa_family } != AF_INET
                {
                    continue;
                }
                let socket = unsafe { &*socket.lpSockaddr.cast::<SOCKADDR_IN>() };
                let ip = Ipv4Addr::from(unsafe { socket.sin_addr.S_un.S_addr }.to_ne_bytes());
                if local_address(ip)
                    && let Some(broadcast) = directed_broadcast(ip, entry.OnLinkPrefixLength)
                    && !output.contains(&(ip, broadcast))
                {
                    output.push((ip, broadcast));
                }
            }
        }
        return Ok(output);
    }
}

fn directed_broadcast(ip: Ipv4Addr, prefix: u8) -> Option<Ipv4Addr> {
    (1..=30).contains(&prefix).then(|| {
        let host_mask = u32::MAX >> prefix;
        Ipv4Addr::from(u32::from(ip) | host_mask)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn respects_interface_prefix_when_discovering() {
        let ip = Ipv4Addr::new(192, 168, 17, 10);
        assert_eq!(
            directed_broadcast(ip, 24),
            Some(Ipv4Addr::new(192, 168, 17, 255))
        );
        assert_eq!(
            directed_broadcast(ip, 20),
            Some(Ipv4Addr::new(192, 168, 31, 255))
        );
        assert_eq!(directed_broadcast(ip, 31), None);
    }
}
