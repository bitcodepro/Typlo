use super::wide;
use image::{
    DynamicImage, ImageDecoder, ImageFormat, ImageReader, RgbaImage, codecs::bmp::BmpDecoder,
};
use serde::{Deserialize, Serialize};
use std::{
    io::{self, Cursor},
    ptr::null_mut,
};
use windows_sys::Win32::{
    Foundation::{GetLastError, GlobalFree, HGLOBAL, HWND, SetLastError},
    System::{
        DataExchange::*,
        Memory::{GMEM_MOVEABLE, GlobalAlloc, GlobalLock, GlobalSize, GlobalUnlock},
    },
};

const UNICODE_TEXT: u32 = 13;
const DIB: u32 = 8;
const DIBV5: u32 = 17;
const HDROP: u32 = 15;

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[repr(u8)]
pub enum Kind {
    Text = 1,
    Image = 2,
    Files = 3,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
enum Format {
    Text,
    Html,
    Rtf,
    Png,
    DibV5,
    Dib,
    Files,
}

impl Format {
    fn id(self) -> u32 {
        match self {
            Self::Text => UNICODE_TEXT,
            Self::Dib => DIB,
            Self::DibV5 => DIBV5,
            Self::Files => HDROP,
            Self::Html => registered("HTML Format"),
            Self::Rtf => registered("Rich Text Format"),
            Self::Png => registered("PNG"),
        }
    }
}

#[derive(Serialize, Deserialize)]
pub struct Clip {
    pub kind: Kind,
    formats: Vec<(Format, Vec<u8>)>,
}

pub(super) const MAX_CAPTION_BYTES: usize = 64 * 1024;

pub(super) struct ImagePreview {
    pub dimensions: (u32, u32),
    pub png: Vec<u8>,
}

impl Clip {
    pub fn has_formatting(&self) -> bool {
        self.formats
            .iter()
            .any(|(format, _)| matches!(format, Format::Html | Format::Rtf))
    }

    pub(super) fn from_legacy(
        kind: u8,
        payload: Vec<u8>,
        extra: Option<&[u8]>,
    ) -> io::Result<Self> {
        use std::collections::HashMap;
        let (kind, formats) = match kind {
            1 => {
                let text = String::from_utf8(payload).map_err(io::Error::other)?;
                if text.is_empty() || text.contains('\0') {
                    return Err(invalid_clip());
                }
                let mut formats = vec![(
                    Format::Text,
                    wide(&text).into_iter().flat_map(u16::to_le_bytes).collect(),
                )];
                if let Some(extra) = extra {
                    let mut data: HashMap<String, Option<String>> =
                        serde_json::from_slice(extra).map_err(io::Error::other)?;
                    for (name, format) in [("html", Format::Html), ("rtf", Format::Rtf)] {
                        if let Some(value) = data
                            .remove(name)
                            .flatten()
                            .filter(|value| !value.is_empty())
                        {
                            let mut bytes = value.into_bytes();
                            if bytes.last() != Some(&0) {
                                bytes.push(0);
                            }
                            formats.push((format, bytes));
                        }
                    }
                }
                (Kind::Text, formats)
            }
            2 if payload.starts_with(b"\x89PNG\r\n\x1a\n") => {
                (Kind::Image, vec![(Format::Png, payload)])
            }
            3 => {
                let paths = String::from_utf8(payload).map_err(io::Error::other)?;
                if paths.is_empty() || paths.contains('\0') {
                    return Err(invalid_clip());
                }
                let mut header = vec![0; 20];
                header[..4].copy_from_slice(&20u32.to_le_bytes());
                header[16..20].copy_from_slice(&1u32.to_le_bytes());
                for path in paths.lines().filter(|path| !path.is_empty()) {
                    header.extend(wide(path).into_iter().flat_map(u16::to_le_bytes));
                }
                header.extend_from_slice(&[0, 0]);
                (Kind::Files, vec![(Format::Files, header)])
            }
            _ => return Err(invalid_clip()),
        };
        Ok(Self { kind, formats })
    }

    #[cfg(feature = "gui")]
    pub fn image(&self) -> io::Result<DynamicImage> {
        self.formats
            .iter()
            .find_map(|(format, bytes)| decode_image(*format, bytes).ok())
            .ok_or_else(invalid_clip)
    }

    pub(super) fn normalize_image(
        mut self,
        limit: usize,
    ) -> io::Result<Option<(Self, Option<ImagePreview>)>> {
        if self.kind != Kind::Image {
            return Ok(Some((self, None)));
        }
        let caption = self
            .formats
            .iter()
            .position(|(format, _)| *format == Format::Text)
            .map(|index| self.formats.remove(index));
        for (format, bytes) in self.formats {
            let Ok(image) = decode_image(format, &bytes) else {
                continue;
            };
            let png = if format == Format::Png {
                bytes
            } else {
                encode_png(&image)?
            };
            if png.len() + caption.as_ref().map_or(0, |(_, text)| text.len()) <= limit {
                let preview = ImagePreview {
                    dimensions: (image.width(), image.height()),
                    png: encode_png(&preview_bitmap(&image))?,
                };
                return Ok(Some((
                    Self {
                        kind: Kind::Image,
                        formats: std::iter::once((Format::Png, png)).chain(caption).collect(),
                    },
                    Some(preview),
                )));
            }
        }
        Ok(None)
    }

    pub fn text(&self) -> String {
        let Some((format, data)) = self.formats.first() else {
            return String::new();
        };
        match format {
            Format::Text => decode_wide(data, false),
            Format::Files if data.len() >= 20 => {
                let offset =
                    u32::from_le_bytes(data[..4].try_into().expect("DROPFILES header present"))
                        as usize;
                data.get(offset..)
                    .filter(|_| offset >= 20)
                    .map_or_else(String::new, |paths| {
                        if data[16..20] == [0; 4] {
                            decode_ansi_paths(paths)
                        } else {
                            decode_wide(paths, true)
                        }
                    })
            }
            _ => String::new(),
        }
    }

    pub fn identity(&self) -> &[u8] {
        self.formats.first().map_or(&[], |(_, data)| data)
    }

    pub(super) fn caption_bytes(&self) -> Option<&[u8]> {
        (self.kind != Kind::Text)
            .then(|| self.formats.last())
            .flatten()
            .filter(|(format, _)| *format == Format::Text)
            .map(|(_, bytes)| bytes.as_slice())
    }

    pub fn editable_text(&self) -> String {
        if self.kind == Kind::Text {
            self.text()
        } else {
            self.caption_bytes()
                .map_or_else(String::new, |data| decode_wide(data, false))
        }
    }

    pub(super) fn set_editable_text(&mut self, text: &str) -> io::Result<()> {
        if text.contains('\0') || (self.kind == Kind::Text && text.trim().is_empty()) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Clipboard text must be nonempty and contain no NUL characters",
            ));
        }
        if self.kind == Kind::Text {
            *self = Self::from_text(text);
        } else {
            let bytes = wide(text)
                .into_iter()
                .flat_map(u16::to_le_bytes)
                .collect::<Vec<_>>();
            if bytes.len() > MAX_CAPTION_BYTES {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Clipboard caption is too long",
                ));
            }
            self.formats.retain(|(format, _)| *format != Format::Text);
            if !text.is_empty() {
                self.formats.push((Format::Text, bytes));
            }
        }
        Ok(())
    }

    pub(crate) fn searchable_text(&self) -> String {
        let text = self.text();
        if let Some(caption) = self.caption_bytes() {
            let caption = decode_wide(caption, false);
            if text.is_empty() {
                caption
            } else {
                format!("{text}\n{caption}")
            }
        } else {
            text
        }
    }

    pub fn size(&self) -> usize {
        self.formats.iter().map(|(_, data)| data.len()).sum()
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut output = Vec::with_capacity(self.size() + self.formats.len() * 5 + 1);
        output.push(self.kind as u8);
        for (format, bytes) in &self.formats {
            let code = match format {
                Format::Text => 1,
                Format::Html => 2,
                Format::Rtf => 3,
                Format::Png => 4,
                Format::DibV5 => 5,
                Format::Dib => 6,
                Format::Files => 7,
            };
            output.push(code);
            output.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
            output.extend_from_slice(bytes);
        }
        output
    }

    pub fn decode(mut data: &[u8]) -> io::Result<Self> {
        let (&kind, rest) = data.split_first().ok_or_else(invalid_clip)?;
        data = rest;
        let kind = match kind {
            1 => Kind::Text,
            2 => Kind::Image,
            3 => Kind::Files,
            _ => return Err(invalid_clip()),
        };
        let mut formats = Vec::new();
        while !data.is_empty() && formats.len() < 7 {
            let header = data.first_chunk::<5>().ok_or_else(invalid_clip)?;
            let format = match header[0] {
                1 => Format::Text,
                2 => Format::Html,
                3 => Format::Rtf,
                4 => Format::Png,
                5 => Format::DibV5,
                6 => Format::Dib,
                7 => Format::Files,
                _ => return Err(invalid_clip()),
            };
            if formats.iter().any(|(existing, _)| *existing == format) {
                return Err(invalid_clip());
            }
            let size =
                u32::from_le_bytes(header[1..].try_into().expect("format length present")) as usize;
            let (bytes, rest) = data[5..].split_at_checked(size).ok_or_else(invalid_clip)?;
            formats.push((format, bytes.to_vec()));
            data = rest;
        }
        if !data.is_empty() || formats.is_empty() {
            return Err(invalid_clip());
        }
        let attachment_formats = if kind != Kind::Text
            && let Some((Format::Text, text)) = formats.last()
        {
            let (units, rest) = text.as_chunks::<2>();
            if text.len() > MAX_CAPTION_BYTES
                || !rest.is_empty()
                || !units.split_last().is_some_and(|(last, body)| {
                    *last == [0, 0]
                        && !body.is_empty()
                        && body.iter().all(|unit| *unit != [0, 0])
                        && char::decode_utf16(body.iter().copied().map(u16::from_le_bytes))
                            .all(|unit| unit.is_ok())
                })
            {
                return Err(invalid_clip());
            }
            &formats[..formats.len() - 1]
        } else {
            &formats[..]
        };
        let valid = match kind {
            Kind::Text => {
                formats[0].0 == Format::Text
                    && formats
                        .iter()
                        .skip(1)
                        .all(|(format, _)| matches!(format, Format::Html | Format::Rtf))
            }
            Kind::Image => {
                !attachment_formats.is_empty()
                    && attachment_formats.iter().all(|(format, _)| {
                        matches!(format, Format::Png | Format::DibV5 | Format::Dib)
                    })
            }
            Kind::Files => {
                attachment_formats.len() == 1 && attachment_formats[0].0 == Format::Files
            }
        };
        if !valid {
            return Err(invalid_clip());
        }
        Ok(Self { kind, formats })
    }

    pub fn from_text(text: &str) -> Self {
        Self {
            kind: Kind::Text,
            formats: vec![(
                Format::Text,
                wide(text).into_iter().flat_map(u16::to_le_bytes).collect(),
            )],
        }
    }
}

pub fn registered(name: &str) -> u32 {
    unsafe { RegisterClipboardFormatW(wide(name).as_ptr()) }
}

pub fn sequence() -> u32 {
    unsafe { GetClipboardSequenceNumber() }
}

const SCREENSHOT_FORMAT: &str = "Typlo Rust Screenshot";

pub(super) fn is_screenshot() -> bool {
    available(registered(SCREENSHOT_FORMAT))
}

pub fn capture(
    expected: u32,
    owner: HWND,
    text_limit: usize,
    image_limit: usize,
) -> io::Result<Option<Clip>> {
    if sequence() != expected {
        return Ok(None);
    }
    let _open = Open::new(null_mut())?;
    let same_owner = || {
        let current = unsafe { GetClipboardOwner() };
        current == owner || (current.is_null() && is_screenshot())
    };
    if !same_owner() || sequence() != expected {
        return Ok(None);
    }
    if available(registered("Typlo Rust History Paste"))
        || available(registered("ExcludeClipboardContentFromMonitorProcessing"))
        || available(registered("Clipboard Viewer Ignore"))
        || read(registered("CanIncludeInClipboardHistory"), 16)
            .is_some_and(|bytes| bytes.starts_with(&[0; 4]))
    {
        return Ok(None);
    }
    let clip = capture_content(text_limit, image_limit, read);
    Ok(clip.filter(|_| same_owner()))
}

fn capture_content(
    text_limit: usize,
    image_limit: usize,
    mut read_format: impl FnMut(u32, usize) -> Option<Vec<u8>>,
) -> Option<Clip> {
    if let Some(bytes) = read_format(HDROP, text_limit) {
        let clip = Clip {
            kind: Kind::Files,
            formats: vec![(Format::Files, bytes)],
        };
        if !clip.text().trim().is_empty() {
            return Some(clip);
        }
    }
    if let Some(mut text) = read_format(UNICODE_TEXT, text_limit)
        && let Some(end) = text
            .as_chunks::<2>()
            .0
            .iter()
            .position(|unit| *unit == [0, 0])
        && end > 0
    {
        text.truncate((end + 1) * 2);
        if !decode_wide(&text, false).trim().is_empty() {
            let mut remaining = text_limit - text.len();
            let mut formats = vec![(Format::Text, text)];
            for format in [Format::Html, Format::Rtf] {
                if let Some(data) = read_format(format.id(), remaining) {
                    remaining -= data.len();
                    formats.push((format, data));
                }
            }
            return Some(Clip {
                kind: Kind::Text,
                formats,
            });
        }
    }
    let png = read_format(Format::Png.id(), image_limit);
    if let Some(png) = png
        && image_dimensions(Format::Png, &png).is_ok()
    {
        return Some(Clip {
            kind: Kind::Image,
            formats: vec![(Format::Png, png)],
        });
    }
    // Raw bitmaps can exceed the PNG limit. Bound the temporary copy and
    // enforce the storage limit after compression.
    let raw_limit = image_limit
        .saturating_mul(4)
        .min(crate::MAX_IMAGE_PIXELS as usize * 4 + 4096);
    [Format::DibV5, Format::Dib].into_iter().find_map(|format| {
        let bytes = read_format(format.id(), raw_limit)?;
        image_dimensions(format, &bytes).ok()?;
        Some(Clip {
            kind: Kind::Image,
            formats: vec![(format, bytes)],
        })
    })
}

fn checked_dimensions(dimensions: (u32, u32)) -> io::Result<()> {
    if dimensions.0 == 0
        || dimensions.1 == 0
        || u64::from(dimensions.0) * u64::from(dimensions.1) > crate::MAX_IMAGE_PIXELS
    {
        return Err(io::Error::other("Clipboard image exceeds pixel limits"));
    }
    Ok(())
}

fn image_dimensions(format: Format, bytes: &[u8]) -> io::Result<(u32, u32)> {
    let dimensions = match format {
        Format::Png => {
            ImageReader::with_format(Cursor::new(bytes), ImageFormat::Png).into_dimensions()
        }
        Format::DibV5 | Format::Dib => BmpDecoder::new_without_file_header(Cursor::new(bytes))
            .map(|decoder| decoder.dimensions()),
        _ => return Err(invalid_clip()),
    }
    .map_err(io::Error::other)?;
    checked_dimensions(dimensions)?;
    Ok(dimensions)
}

fn decode_image(format: Format, bytes: &[u8]) -> io::Result<DynamicImage> {
    match format {
        Format::Png => {
            image_dimensions(format, bytes)?;
            ImageReader::with_format(Cursor::new(bytes), ImageFormat::Png).decode()
        }
        Format::DibV5 | Format::Dib => {
            image_dimensions(format, bytes)?;
            ImageReader::with_format(Cursor::new(dib_as_bmp(bytes)?), ImageFormat::Bmp).decode()
        }
        _ => return Err(invalid_clip()),
    }
    .map_err(io::Error::other)
}

fn dib_as_bmp(bytes: &[u8]) -> io::Result<Vec<u8>> {
    let header = bytes.first_chunk::<12>().ok_or_else(invalid_clip)?;
    let header_len = u32::from_le_bytes(header[..4].try_into().unwrap()) as usize;
    let (colors, entry_size, masks) = if header_len == 12 {
        let depth = u16::from_le_bytes([header[10], header[11]]);
        (if depth <= 8 { 1 << depth } else { 0 }, 3, 0)
    } else {
        let info = bytes.first_chunk::<40>().ok_or_else(invalid_clip)?;
        let depth = u16::from_le_bytes([info[14], info[15]]);
        let compression = u32::from_le_bytes(info[16..20].try_into().unwrap());
        let colors = u32::from_le_bytes(info[32..36].try_into().unwrap()) as usize;
        let colors = if colors == 0 && (1..=8).contains(&depth) {
            1 << depth
        } else {
            colors
        };
        // V4 and V5 embed their color masks in the header. The BMP decoder
        // needs an explicit pixel offset for these packed clipboard bitmaps.
        (
            colors,
            4,
            if header_len == 40 && compression == 3 {
                12
            } else {
                0
            },
        )
    };
    let offset = colors
        .checked_mul(entry_size)
        .and_then(|palette| header_len.checked_add(masks)?.checked_add(palette))
        .filter(|&offset| offset < bytes.len())
        .ok_or_else(invalid_clip)?;
    let mut bmp = Vec::with_capacity(14 + bytes.len());
    bmp.extend_from_slice(b"BM");
    bmp.extend_from_slice(&((14 + bytes.len()) as u32).to_le_bytes());
    bmp.extend_from_slice(&[0; 4]);
    bmp.extend_from_slice(&((14 + offset) as u32).to_le_bytes());
    bmp.extend_from_slice(bytes);
    Ok(bmp)
}

pub(super) fn preview_bitmap(image: &DynamicImage) -> DynamicImage {
    image.thumbnail(image.width().min(320), image.height().min(320))
}

fn encode_png(image: &DynamicImage) -> io::Result<Vec<u8>> {
    let mut output = Cursor::new(Vec::new());
    image
        .write_to(&mut output, ImageFormat::Png)
        .map_err(io::Error::other)?;
    Ok(output.into_inner())
}

fn bitmap(image: &RgbaImage) -> io::Result<Vec<u8>> {
    use windows_sys::Win32::Graphics::Gdi::{BI_BITFIELDS, BITMAPV5HEADER, LCS_GM_IMAGES};
    let (width, height) = image.dimensions();
    checked_dimensions((width, height))?;
    let header = BITMAPV5HEADER {
        bV5Size: size_of::<BITMAPV5HEADER>() as u32,
        bV5Width: width as i32,
        bV5Height: height as i32,
        bV5Planes: 1,
        bV5BitCount: 32,
        bV5Compression: BI_BITFIELDS,
        bV5SizeImage: image.as_raw().len() as u32,
        bV5RedMask: 0x00ff0000,
        bV5GreenMask: 0x0000ff00,
        bV5BlueMask: 0x000000ff,
        bV5AlphaMask: 0xff000000,
        bV5CSType: 0x7352_4742, // LCS_sRGB
        bV5Intent: LCS_GM_IMAGES as u32,
        ..Default::default()
    };
    let mut bytes = Vec::with_capacity(size_of::<BITMAPV5HEADER>() + image.as_raw().len());
    // The Win32 header consists of initialized 16-bit and 32-bit integers.
    let header = unsafe {
        std::slice::from_raw_parts(
            (&raw const header).cast::<u8>(),
            size_of::<BITMAPV5HEADER>(),
        )
    };
    bytes.extend_from_slice(header);
    for row in image.as_raw().chunks_exact(width as usize * 4).rev() {
        for [r, g, b, a] in row.as_chunks::<4>().0 {
            bytes.extend_from_slice(&[*b, *g, *r, *a]);
        }
    }
    Ok(bytes)
}

#[cfg(feature = "gui")]
pub(crate) fn publish_screenshot(image: &RgbaImage, owner: HWND) -> io::Result<()> {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DestroyWindow, HWND_MESSAGE,
    };
    let temporary = owner.is_null();
    let owner = if temporary {
        unsafe {
            CreateWindowExW(
                0,
                wide("STATIC").as_ptr(),
                null_mut(),
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                null_mut(),
                null_mut(),
                null_mut(),
            )
        }
    } else {
        owner
    };
    if owner.is_null() {
        return Err(io::Error::last_os_error());
    }
    let result = (|| {
        use image::ImageEncoder;
        let mut png = Vec::new();
        image::codecs::png::PngEncoder::new(&mut png)
            .write_image(
                image.as_raw(),
                image.width(),
                image.height(),
                image::ExtendedColorType::Rgba8,
            )
            .map_err(io::Error::other)?;
        let dib = bitmap(image)?;
        write_clipboard(
            owner,
            vec![
                (Format::Png.id(), Global::new(&png)?),
                (DIBV5, Global::new(&dib)?),
                (registered(SCREENSHOT_FORMAT), Global::new(&[1, 0, 0, 0])?),
            ],
        )
    })();
    if temporary {
        unsafe { DestroyWindow(owner) };
    }
    result.map(|_| ())
}

pub fn publish(owner: HWND, clip: &Clip, plain: bool) -> io::Result<u32> {
    if owner.is_null() {
        return Err(io::Error::other("Clipboard owner is missing"));
    }
    let mut buffers: Vec<_> = clip
        .formats
        .iter()
        .filter(|(format, _)| !plain || !matches!(format, Format::Html | Format::Rtf))
        .map(|(format, bytes)| Ok((format.id(), Global::new(bytes)?)))
        .collect::<io::Result<_>>()?;
    if clip.kind == Kind::Image
        && !clip
            .formats
            .iter()
            .any(|(format, _)| matches!(format, Format::Dib | Format::DibV5))
        && let Some((format, bytes)) = clip
            .formats
            .iter()
            .find(|(format, _)| *format == Format::Png)
    {
        let image = decode_image(*format, bytes)?.into_rgba8();
        buffers.push((DIBV5, Global::new(&bitmap(&image)?)?));
    }
    if clip.kind == Kind::Files {
        buffers.push((
            registered("Preferred DropEffect"),
            Global::new(&1u32.to_le_bytes())?,
        ));
    }
    buffers.push((
        registered("Typlo Rust History Paste"),
        Global::new(&[1, 0, 0, 0])?,
    ));
    write_clipboard(owner, buffers)
}

fn write_clipboard(owner: HWND, buffers: Vec<(u32, Global)>) -> io::Result<u32> {
    if buffers.iter().any(|(format, _)| *format == 0) {
        return Err(io::Error::last_os_error());
    }
    let open = Open::new(owner)?;
    if unsafe { EmptyClipboard() } == 0 {
        return Err(io::Error::last_os_error());
    }
    for (format, mut data) in buffers {
        if unsafe { SetClipboardData(format, data.0) }.is_null() {
            return Err(io::Error::last_os_error());
        }
        data.0 = null_mut();
    }
    drop(open);
    if unsafe { GetClipboardOwner() } != owner {
        return Err(clipboard_changed());
    }
    Ok(sequence())
}

pub struct TemporaryText {
    owner: HWND,
    holder: HWND,
    sequence: u32,
    originals: Vec<(u32, Global)>,
    published: bool,
}

impl TemporaryText {
    pub fn capture(owner: HWND) -> io::Result<Self> {
        let _open = Open::new(owner)?;
        let mut originals = Vec::new();
        let mut id = 0;
        let mut budget = 64 * 1024 * 1024;
        loop {
            id = unsafe {
                SetLastError(0);
                EnumClipboardFormats(id)
            };
            if id == 0 {
                if unsafe { GetLastError() } != 0 {
                    return Err(io::Error::last_os_error());
                }
                break;
            }
            if originals.len() >= 128 {
                return Err(unrestorable());
            }
            // Windows can recreate this bitmap when a DIB is restored.
            if id == 2 && (available(DIBV5) || available(DIB)) {
                continue;
            }
            if id < 0xc000
                && !matches!(id, 1 | 4 | 5 | 6 | 7 | 8 | 10 | 11 | 12 | 13 | 15 | 16 | 17)
            {
                return Err(unrestorable());
            }
            let data = read(id, budget).ok_or_else(unrestorable)?;
            budget -= data.len();
            originals.push((id, Global::new(&data)?));
        }
        Ok(Self {
            owner,
            holder: owner,
            sequence: sequence(),
            originals,
            published: false,
        })
    }

    pub fn publish(&mut self, replacement: &[u16]) -> io::Result<()> {
        if replacement.contains(&0) {
            return Err(invalid_clip());
        }
        let bytes: Vec<_> = replacement
            .iter()
            .copied()
            .chain([0])
            .flat_map(u16::to_le_bytes)
            .collect();
        let mut text = Global::new(&bytes)?;
        let mut private = Global::new(&[0; 4])?;
        let format = registered("CanIncludeInClipboardHistory");
        if format == 0 {
            return Err(io::Error::last_os_error());
        }
        let open = Open::new(self.owner)?;
        if self.sequence != sequence() {
            return Err(clipboard_changed());
        }
        if unsafe { EmptyClipboard() } == 0 {
            return Err(io::Error::last_os_error());
        }
        self.published = true;
        self.holder = self.owner;
        let result = text
            .transfer(UNICODE_TEXT)
            .and_then(|()| private.transfer(format));
        self.sequence = sequence();
        drop(open);
        if unsafe { GetClipboardOwner() } == self.owner {
            self.sequence = sequence();
        }
        result
    }

    pub fn still_owns(&self) -> bool {
        self.published
            && sequence() == self.sequence
            && unsafe { GetClipboardOwner() } == self.holder
    }

    pub(super) fn unchanged(&self) -> bool {
        if self.published {
            self.still_owns()
        } else {
            sequence() == self.sequence
        }
    }

    pub(super) fn copied_text(
        &mut self,
        previous: u32,
        accept: impl FnOnce(HWND) -> bool,
    ) -> io::Result<Option<String>> {
        self.copied_text_raw(previous, accept)
            .map(|text| text.map(|text| text.replace("\r\n", "\n").replace('\r', "\n")))
    }

    pub(super) fn copied_text_raw(
        &mut self,
        previous: u32,
        accept: impl FnOnce(HWND) -> bool,
    ) -> io::Result<Option<String>> {
        if sequence() == previous {
            return Ok(None);
        }
        let _open = Open::new(self.owner)?;
        let current = sequence();
        let holder = unsafe { GetClipboardOwner() };
        if current == previous {
            return Ok(None);
        }
        if holder.is_null() || !accept(holder) {
            return Err(clipboard_changed());
        }
        self.sequence = current;
        self.holder = holder;
        self.published = true;
        // CRLF can double the UTF-16 length of a normalized multiline range.
        let data = read(UNICODE_TEXT, 256 * 1024).ok_or_else(unrestorable)?;
        let mut units = data
            .as_chunks::<2>()
            .0
            .iter()
            .map(|unit| u16::from_le_bytes([unit[0], unit[1]]));
        let text = String::from_utf16(
            &units
                .by_ref()
                .take_while(|&unit| unit != 0)
                .collect::<Vec<_>>(),
        )
        .map_err(io::Error::other)?;
        Ok(Some(text))
    }

    pub fn restore(&mut self) -> io::Result<()> {
        if !self.still_owns() {
            return Ok(());
        }
        let _open = Open::new(self.owner)?;
        if !self.still_owns() {
            return Ok(());
        }
        if unsafe { EmptyClipboard() } == 0 {
            return Err(io::Error::last_os_error());
        }
        self.published = false;
        for (format, data) in &mut self.originals {
            data.transfer(*format)?;
        }
        Ok(())
    }
}

impl Drop for TemporaryText {
    fn drop(&mut self) {
        if self.published {
            for _ in 0..4 {
                if self.restore().is_ok() {
                    break;
                }
                std::thread::sleep(std::time::Duration::from_millis(8));
            }
        }
    }
}

fn unrestorable() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "Clipboard has a format that cannot be restored safely",
    )
}

fn clipboard_changed() -> io::Error {
    io::Error::new(
        io::ErrorKind::Interrupted,
        "Clipboard changed during editing",
    )
}

fn decode_ansi_paths(bytes: &[u8]) -> String {
    use windows_sys::Win32::Globalization::{CP_ACP, MultiByteToWideChar};
    let end = bytes
        .windows(2)
        .position(|pair| pair == [0, 0])
        .unwrap_or(bytes.len());
    let bytes = &bytes[..end];
    let Ok(count) = i32::try_from(bytes.len()) else {
        return String::new();
    };
    let size = unsafe { MultiByteToWideChar(CP_ACP, 0, bytes.as_ptr(), count, null_mut(), 0) };
    if size <= 0 {
        return String::new();
    }
    let mut units = vec![0; size as usize];
    let written =
        unsafe { MultiByteToWideChar(CP_ACP, 0, bytes.as_ptr(), count, units.as_mut_ptr(), size) };
    if written != size {
        return String::new();
    }
    String::from_utf16_lossy(&units).replace('\0', "\n")
}

fn decode_wide(bytes: &[u8], multiple: bool) -> String {
    let mut data = Vec::new();
    for &unit in bytes.as_chunks::<2>().0 {
        let word = u16::from_le_bytes(unit);
        if word == 0 {
            if !multiple || data.last() == Some(&u16::from(b'\n')) {
                break;
            }
            data.push(u16::from(b'\n'));
        } else {
            data.push(word);
        }
    }
    if multiple && data.last() == Some(&u16::from(b'\n')) {
        data.pop();
    }
    String::from_utf16_lossy(&data)
}

fn invalid_clip() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid clipboard data")
}

fn available(format: u32) -> bool {
    format != 0 && unsafe { IsClipboardFormatAvailable(format) } != 0
}

fn read(format: u32, limit: usize) -> Option<Vec<u8>> {
    if !available(format) {
        return None;
    }
    let handle = unsafe { GetClipboardData(format) };
    if handle.is_null() {
        return None;
    }
    let size = unsafe { GlobalSize(handle) };
    if size == 0 || size > limit {
        return None;
    }
    let source = unsafe { GlobalLock(handle) };
    if source.is_null() {
        return None;
    }
    let result = unsafe { std::slice::from_raw_parts(source.cast::<u8>(), size) }.to_vec();
    unsafe {
        GlobalUnlock(handle);
    }
    Some(result)
}

struct Open;
impl Open {
    fn new(owner: HWND) -> io::Result<Self> {
        if unsafe { OpenClipboard(owner) } == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(Self)
        }
    }
}
impl Drop for Open {
    fn drop(&mut self) {
        unsafe {
            CloseClipboard();
        }
    }
}

struct Global(HGLOBAL);
impl Global {
    fn new(bytes: &[u8]) -> io::Result<Self> {
        let global = Self(unsafe { GlobalAlloc(GMEM_MOVEABLE, bytes.len()) });
        if global.0.is_null() {
            return Err(io::Error::last_os_error());
        }
        let dest = unsafe { GlobalLock(global.0) };
        if dest.is_null() {
            return Err(io::Error::last_os_error());
        }
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), dest.cast::<u8>(), bytes.len());
            GlobalUnlock(global.0);
        }
        Ok(global)
    }

    fn transfer(&mut self, format: u32) -> io::Result<()> {
        if unsafe { SetClipboardData(format, self.0) }.is_null() {
            return Err(io::Error::last_os_error());
        }
        self.0 = null_mut();
        Ok(())
    }
}
impl Drop for Global {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                GlobalFree(self.0);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::Rgba;

    #[test]
    fn images_survive_empty_text_and_invalid_file_formats() {
        let image = DynamicImage::ImageRgba8(RgbaImage::from_pixel(4, 3, Rgba([220, 12, 30, 255])));
        let png = encode_png(&image).unwrap();
        let png_id = Format::Png.id();
        for text in [vec![0, 0], vec![b' ', 0, 0, 0], vec![b'a', 0]] {
            let clip = capture_content(1024, 1024, |format, _| match format {
                HDROP => Some(vec![0; 20]),
                UNICODE_TEXT => Some(text.clone()),
                id if id == png_id => Some(png.clone()),
                _ => None,
            })
            .unwrap();
            assert_eq!(clip.kind, Kind::Image);
            assert_eq!(clip.identity(), &png);
        }

        let text = Clip::from_text("actual text");
        let clip = capture_content(1024, 1024, |format, _| match format {
            UNICODE_TEXT => Some(text.identity().to_vec()),
            id if id == png_id => Some(png.clone()),
            _ => None,
        })
        .unwrap();
        assert_eq!(clip.text(), "actual text");
    }

    #[test]
    fn dib_larger_than_the_png_limit_is_encoded_with_alpha_and_a_preview() {
        let mut image = RgbaImage::from_pixel(64, 48, Rgba([220, 12, 30, 100]));
        image.put_pixel(0, 0, Rgba([255, 0, 0, 255]));
        image.put_pixel(63, 47, Rgba([0, 0, 255, 200]));
        let bitmap = bitmap(&image).unwrap();
        let limit = 4096;
        assert!(bitmap.len() > limit);
        let clip = capture_content(1024, limit, |format, size| {
            (format == DIBV5 && bitmap.len() <= size).then(|| bitmap.clone())
        })
        .unwrap();
        let (clip, preview) = clip.normalize_image(limit).unwrap().unwrap();
        assert_eq!(clip.formats[0].0, Format::Png);
        assert!(clip.size() < limit);
        assert_eq!(
            decode_image(Format::Png, clip.identity())
                .unwrap()
                .into_rgba8(),
            image
        );
        let preview = preview.unwrap();
        assert_eq!(preview.dimensions, (64, 48));
        assert_eq!(
            decode_image(Format::Png, &preview.png)
                .unwrap()
                .into_rgba8(),
            image
        );
    }

    #[test]
    fn reads_ansi_file_drop_paths() {
        let mut data = vec![0; 20];
        data[0] = 20;
        data.extend_from_slice(b"C:\\one.txt\0D:\\two.txt\0\0");
        let files = capture_content(1024, 1024, |format, _| {
            (format == HDROP).then(|| data.clone())
        })
        .unwrap();
        assert_eq!(files.text(), "C:\\one.txt\nD:\\two.txt");
    }
}
