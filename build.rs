use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rerun-if-changed=assets/Typlo.ico");
    let target = env::var("TARGET").expect("Cargo target");
    if !target.ends_with("windows-msvc") {
        return;
    }
    let icon = include_bytes!("assets/Typlo.ico");
    let count = u16::from_le_bytes([icon[4], icon[5]]);
    let mut resource = Vec::new();
    entry(&mut resource, 0, 0, &[]);
    let mut group = icon[..6].to_vec();
    for (index, image) in icon[6..6 + 16 * usize::from(count)]
        .as_chunks::<16>()
        .0
        .iter()
        .enumerate()
    {
        let id = u16::try_from(index + 1).expect("ICO image ID");
        let length = u32::from_le_bytes(image[8..12].try_into().unwrap()) as usize;
        let start = u32::from_le_bytes(image[12..16].try_into().unwrap()) as usize;
        entry(&mut resource, 3, id, &icon[start..start + length]);
        group.extend_from_slice(&image[..12]);
        group.extend_from_slice(&id.to_le_bytes());
    }
    entry(&mut resource, 14, 1, &group);
    let path =
        PathBuf::from(env::var_os("OUT_DIR").expect("Cargo output directory")).join("Typlo.res");
    fs::write(&path, resource).expect("write Typlo icon resources");
    println!("cargo:rustc-link-arg-bins={}", path.display());
}

fn entry(output: &mut Vec<u8>, kind: u16, id: u16, data: &[u8]) {
    // Numeric resource type and name are encoded as a 0xffff marker and a 16-bit ID.
    output.extend_from_slice(
        &u32::try_from(data.len())
            .expect("resource length")
            .to_le_bytes(),
    );
    output.extend_from_slice(&32u32.to_le_bytes());
    for word in [0xffffu16, kind, 0xffff, id] {
        output.extend_from_slice(&word.to_le_bytes());
    }
    output.extend_from_slice(&[0; 16]);
    output.extend_from_slice(data);
    output.resize(output.len().next_multiple_of(4), 0);
}
