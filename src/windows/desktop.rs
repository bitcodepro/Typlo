use crate::capture::MAX_PIXELS;
use image::RgbaImage;
use std::{io, ptr::null_mut};
use windows_sys::Win32::{
    Foundation::RECT,
    Graphics::Gdi::*,
    UI::WindowsAndMessaging::{
        GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN,
        SM_YVIRTUALSCREEN,
    },
};

pub struct Desktop {
    pub x: i32,
    pub y: i32,
    pub image: RgbaImage,
}

pub fn capture(region: RECT) -> io::Result<Desktop> {
    let (left, top, desktop_width, desktop_height) = unsafe {
        (
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN),
        )
    };
    let x = region.left.max(left);
    let y = region.top.max(top);
    let width = region
        .right
        .min(left.saturating_add(desktop_width))
        .saturating_sub(x);
    let height = region
        .bottom
        .min(top.saturating_add(desktop_height))
        .saturating_sub(y);
    if width <= 0 || height <= 0 || width as u64 * height as u64 > MAX_PIXELS {
        return Err(io::Error::other("Empty or oversized screenshot region"));
    }
    let screen = Screen(unsafe { GetDC(null_mut()) });
    if screen.0.is_null() {
        return Err(io::Error::last_os_error());
    }
    let compatible = MemoryDc(unsafe { CreateCompatibleDC(screen.0) });
    if compatible.0.is_null() {
        return Err(io::Error::last_os_error());
    }
    let format = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut pixels = null_mut();
    let bitmap = Bitmap(unsafe {
        CreateDIBSection(
            compatible.0,
            &format,
            DIB_RGB_COLORS,
            &mut pixels,
            null_mut(),
            0,
        )
    });
    if bitmap.0.is_null() || pixels.is_null() {
        return Err(io::Error::last_os_error());
    }
    let previous = unsafe { SelectObject(compatible.0, bitmap.0) };
    if previous.is_null() || previous as isize == -1 {
        return Err(io::Error::last_os_error());
    }
    let copied = unsafe {
        let result = BitBlt(
            compatible.0,
            0,
            0,
            width,
            height,
            screen.0,
            x,
            y,
            SRCCOPY | CAPTUREBLT,
        );
        GdiFlush();
        SelectObject(compatible.0, previous);
        result
    };
    if copied == 0 {
        return Err(io::Error::last_os_error());
    }
    let mut data = unsafe {
        std::slice::from_raw_parts(pixels.cast::<u8>(), width as usize * height as usize * 4)
    }
    .to_vec();
    for pixel in data.as_chunks_mut::<4>().0 {
        pixel.swap(0, 2);
        pixel[3] = 255;
    }
    let image = RgbaImage::from_raw(width as u32, height as u32, data)
        .ok_or_else(|| io::Error::other("Invalid desktop image"))?;
    Ok(Desktop { x, y, image })
}

struct Screen(HDC);
impl Drop for Screen {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                ReleaseDC(null_mut(), self.0);
            }
        }
    }
}

struct MemoryDc(HDC);
impl Drop for MemoryDc {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                DeleteDC(self.0);
            }
        }
    }
}

struct Bitmap(HBITMAP);
impl Drop for Bitmap {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                DeleteObject(self.0);
            }
        }
    }
}
