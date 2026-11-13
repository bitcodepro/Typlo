use image::{RgbaImage, imageops::FilterType};
use std::io;
use windows::{
    Graphics::Imaging::{BitmapPixelFormat, SoftwareBitmap},
    Media::Ocr::OcrEngine,
    Security::Cryptography::CryptographicBuffer,
    Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize},
};

pub fn available() -> bool {
    static AVAILABLE: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *AVAILABLE.get_or_init(|| {
        Runtime::new().is_ok_and(|_runtime| OcrEngine::TryCreateFromUserProfileLanguages().is_ok())
    })
}

pub fn recognize(mut image: RgbaImage) -> io::Result<String> {
    let _runtime = Runtime::new()?;
    let engine = OcrEngine::TryCreateFromUserProfileLanguages()
        .map_err(|error| io::Error::other(format!("OCR Windows недоступен ({error})")))?;
    let max = OcrEngine::MaxImageDimension().map_err(io::Error::other)?;
    let largest = image.width().max(image.height());
    if largest == 0 || max == 0 {
        return Err(io::Error::other("Некорректный размер изображения"));
    }
    if largest > max {
        let dimension =
            |value| ((u64::from(value) * u64::from(max) / u64::from(largest)) as u32).max(1);
        image = image::imageops::resize(
            &image,
            dimension(image.width()),
            dimension(image.height()),
            FilterType::Triangle,
        );
    }
    let (width, height) = image.dimensions();
    let mut pixels = image.into_raw();
    for pixel in pixels.as_chunks_mut::<4>().0 {
        let alpha = u16::from(pixel[3]);
        for channel in &mut pixel[..3] {
            *channel = ((u16::from(*channel) * alpha + 255 * (255 - alpha) + 127) / 255) as u8;
        }
        pixel.swap(0, 2);
        pixel[3] = 255;
    }
    let buffer = CryptographicBuffer::CreateFromByteArray(&pixels).map_err(io::Error::other)?;
    drop(pixels);
    let bitmap = SoftwareBitmap::CreateCopyFromBuffer(
        &buffer,
        BitmapPixelFormat::Bgra8,
        width as i32,
        height as i32,
    )
    .map_err(io::Error::other)?;
    drop(buffer);
    let result = engine
        .RecognizeAsync(&bitmap)
        .map_err(io::Error::other)?
        .join()
        .map_err(io::Error::other)?;
    Ok(result.Text().map_err(io::Error::other)?.to_string())
}

struct Runtime;
impl Runtime {
    fn new() -> io::Result<Self> {
        unsafe { RoInitialize(RO_INIT_MULTITHREADED) }.map_err(io::Error::other)?;
        Ok(Self)
    }
}
impl Drop for Runtime {
    fn drop(&mut self) {
        unsafe {
            RoUninitialize();
        }
    }
}
