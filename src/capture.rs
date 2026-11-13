use image::{Rgba, RgbaImage, codecs::jpeg::JpegEncoder};
use std::{
    fs::File,
    io::{self, BufWriter, Write},
    path::Path,
};

pub const MAX_PIXELS: u64 = crate::MAX_IMAGE_PIXELS;
pub const MAX_POINTS: usize = 4096;
pub const JPEG_QUALITY: u8 = 100;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Point {
    pub x: f32,
    pub y: f32,
}

pub fn crop(image: &RgbaImage, points: &[Point], free: bool) -> io::Result<RgbaImage> {
    if points.len() < if free { 3 } else { 2 }
        || points.len() > MAX_POINTS
        || points
            .iter()
            .any(|point| !point.x.is_finite() || !point.y.is_finite())
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Invalid screenshot region",
        ));
    }
    let width = image.width();
    let height = image.height();
    let (mut min_x, mut min_y, mut max_x, mut max_y) = (width as f32, height as f32, 0f32, 0f32);
    for point in points {
        min_x = min_x.min(point.x);
        min_y = min_y.min(point.y);
        max_x = max_x.max(point.x);
        max_y = max_y.max(point.y);
    }
    let left = min_x.floor().clamp(0., width as f32) as u32;
    let top = min_y.floor().clamp(0., height as f32) as u32;
    let right = max_x.ceil().clamp(0., width as f32) as u32;
    let bottom = max_y.ceil().clamp(0., height as f32) as u32;
    if right <= left
        || bottom <= top
        || u64::from(right - left) * u64::from(bottom - top) > MAX_PIXELS
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Empty or oversized screenshot region",
        ));
    }
    if !free {
        return Ok(
            image::imageops::crop_imm(image, left, top, right - left, bottom - top).to_image(),
        );
    }
    let edges: Vec<_> = points
        .iter()
        .copied()
        .zip(points.iter().copied().cycle().skip(1))
        .filter(|(a, b)| a.y != b.y)
        .collect();
    let mut intersections = Vec::new();
    let mut output = RgbaImage::new(right - left, bottom - top);
    let mut visible = false;
    for row in top..bottom {
        let y = row as f32 + 0.5;
        intersections.clear();
        intersections.extend(
            edges
                .iter()
                .filter(|(a, b)| y >= a.y.min(b.y) && y < a.y.max(b.y))
                .map(|(a, b)| a.x + (y - a.y) * (b.x - a.x) / (b.y - a.y)),
        );
        intersections.sort_by(f32::total_cmp);
        for pair in intersections.as_chunks::<2>().0 {
            let begin = (pair[0] - 0.5).ceil().clamp(left as f32, right as f32) as u32;
            let end = (pair[1] - 0.5).ceil().clamp(left as f32, right as f32) as u32;
            visible |= end > begin;
            for col in begin..end {
                output.put_pixel(col - left, row - top, *image.get_pixel(col, row));
            }
        }
    }
    if !visible {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Empty free-form screenshot",
        ));
    }
    Ok(output)
}

pub fn write_jpeg(image: &RgbaImage, path: &Path) -> io::Result<()> {
    let mut rgb = image::RgbImage::new(image.width(), image.height());
    for (input, output) in image.pixels().zip(rgb.pixels_mut()) {
        let alpha = u32::from(input.0[3]);
        for channel in 0..3 {
            output.0[channel] =
                ((u32::from(input.0[channel]) * alpha + 255 * (255 - alpha) + 127) / 255) as u8;
        }
    }
    write_atomically(path, |output| {
        JpegEncoder::new_with_quality(output, JPEG_QUALITY)
            .encode_image(&rgb)
            .map_err(io::Error::other)
    })
}

pub fn remember(image: &RgbaImage, profile: &Path) -> io::Result<()> {
    std::fs::create_dir_all(profile)?;
    let mut png = io::Cursor::new(Vec::new());
    image
        .write_to(&mut png, image::ImageFormat::Png)
        .map_err(io::Error::other)?;
    #[cfg(windows)]
    let png = crate::windows::history::protect(&png.into_inner())?;
    #[cfg(not(windows))]
    let png = png.into_inner();
    write_atomically(&last_screenshot(profile), |writer| writer.write_all(&png))
}

pub fn load_last(profile: &Path) -> io::Result<RgbaImage> {
    let path = last_screenshot(profile);
    let (path, protected) = if path.try_exists()? {
        (path, cfg!(windows))
    } else {
        (profile.join("latest-screenshot.png"), false)
    };
    if std::fs::metadata(&path)?.len() > MAX_PIXELS * 5 {
        return Err(io::Error::other("Screenshot exceeds the image limit"));
    }
    let png = std::fs::read(&path)?;
    #[cfg(windows)]
    let png = if protected {
        crate::windows::history::unprotect(&png)?
    } else {
        png
    };
    #[cfg(not(windows))]
    let _ = protected;
    let reader = || image::ImageReader::with_format(io::Cursor::new(&png), image::ImageFormat::Png);
    let (width, height) = reader().into_dimensions().map_err(io::Error::other)?;
    if u64::from(width) * u64::from(height) > MAX_PIXELS {
        return Err(io::Error::other("Screenshot exceeds the image limit"));
    }
    Ok(reader().decode().map_err(io::Error::other)?.into_rgba8())
}

fn last_screenshot(profile: &Path) -> std::path::PathBuf {
    profile.join(if cfg!(windows) {
        "latest-screenshot.png.dpapi"
    } else {
        "latest-screenshot.png"
    })
}

#[cfg(windows)]
pub(crate) fn forget_last(profile: &Path) -> io::Result<()> {
    // Older previews also accepted an unencrypted original as a fallback.
    for path in [
        profile.join("latest-screenshot.png"),
        last_screenshot(profile),
    ] {
        match std::fs::remove_file(path) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }
    }
    Ok(())
}

fn write_atomically(
    path: &Path,
    write: impl FnOnce(&mut BufWriter<File>) -> io::Result<()>,
) -> io::Result<()> {
    use std::time::{SystemTime, UNIX_EPOCH};
    let time = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(io::Error::other)?
        .as_nanos();
    let temporary = path.with_file_name(format!(".typlo-{}-{time}.tmp", std::process::id()));
    let file = File::options()
        .write(true)
        .create_new(true)
        .open(&temporary)?;
    let mut writer = BufWriter::new(file);
    let result = write(&mut writer)
        .and_then(|()| writer.flush())
        .and_then(|()| writer.get_ref().sync_all());
    drop(writer);
    let result = result.and_then(|()| std::fs::rename(&temporary, path));
    if result.is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Tool {
    Pen,
    Marker,
    Arrow,
    Rectangle,
}

pub struct Stroke {
    pub tool: Tool,
    pub color: [u8; 3],
    pub width: f32,
    pub points: Vec<Point>,
}

impl Stroke {
    pub fn paths(&self) -> Vec<Vec<Point>> {
        let Some((&first, &last)) = self.points.first().zip(self.points.last()) else {
            return Vec::new();
        };
        match self.tool {
            Tool::Pen | Tool::Marker => vec![self.points.clone()],
            Tool::Rectangle => vec![vec![
                first,
                Point {
                    x: last.x,
                    y: first.y,
                },
                last,
                Point {
                    x: first.x,
                    y: last.y,
                },
                first,
            ]],
            Tool::Arrow => {
                let dx = last.x - first.x;
                let dy = last.y - first.y;
                let angle = dy.atan2(dx);
                let head = (self.width * 3.5).max(12.).min(dx.hypot(dy) * 0.6);
                let tip = |angle: f32| Point {
                    x: last.x - head * angle.cos(),
                    y: last.y - head * angle.sin(),
                };
                vec![
                    vec![first, last],
                    vec![tip(angle - 0.5), last, tip(angle + 0.5)],
                ]
            }
        }
    }

    pub fn alpha(&self) -> u8 {
        if self.tool == Tool::Marker { 96 } else { 255 }
    }

    pub fn draw(&self, image: &mut RgbaImage) {
        let Some(first) = self.points.first() else {
            return;
        };
        let mut mask = std::collections::HashMap::<(u32, u32), u8>::new();
        let radius = self.width / 2.;
        for path in self.paths() {
            for (a, b) in path.iter().zip(path.iter().skip(1)) {
                let dx = b.x - a.x;
                let dy = b.y - a.y;
                let length = dx * dx + dy * dy;
                let min_x = (a.x.min(b.x) - radius - 1.).max(0.) as u32;
                let min_y = (a.y.min(b.y) - radius - 1.).max(0.) as u32;
                let max_x = (a.x.max(b.x) + radius + 1.)
                    .ceil()
                    .min(image.width() as f32) as u32;
                let max_y = (a.y.max(b.y) + radius + 1.)
                    .ceil()
                    .min(image.height() as f32) as u32;
                // The shorter axis bounds each scanline, even for a long diagonal.
                for row in min_y..max_y {
                    let y = row as f32 + 0.5 - a.y;
                    let (begin, end) = if dy.abs() > 1. {
                        let crossing = (y / dy).clamp(0., 1.) * dx + a.x;
                        let margin = (radius + 1.) * (1. + (dx / dy).abs());
                        (
                            min_x.max((crossing - margin).max(0.) as u32),
                            max_x.min((crossing + margin).ceil() as u32),
                        )
                    } else {
                        (min_x, max_x)
                    };
                    for col in begin..end {
                        let x = col as f32 + 0.5 - a.x;
                        let t = if length == 0. {
                            0.
                        } else {
                            ((x * dx + y * dy) / length).clamp(0., 1.)
                        };
                        let distance = (x - dx * t).hypot(y - dy * t);
                        let alpha = ((radius + 0.5 - distance).clamp(0., 1.)
                            * f32::from(self.alpha()))
                        .round() as u8;
                        if alpha != 0 {
                            mask.entry((col, row))
                                .and_modify(|old| *old = (*old).max(alpha))
                                .or_insert(alpha);
                        }
                    }
                }
            }
        }
        if self.points.len() == 1
            && first.x >= 0.
            && first.y >= 0.
            && (first.x as u32) < image.width()
            && (first.y as u32) < image.height()
        {
            mask.insert((first.x as u32, first.y as u32), self.alpha());
        }
        for ((col, row), alpha) in mask {
            use image::Pixel;
            image.get_pixel_mut(col, row).blend(&Rgba([
                self.color[0],
                self.color[1],
                self.color[2],
                alpha,
            ]));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn free_form_crop_preserves_pixels_inside_and_makes_outside_transparent() {
        let source = RgbaImage::from_pixel(10, 10, Rgba([5, 10, 15, 255]));
        let triangle = [
            Point { x: 2., y: 2. },
            Point { x: 8., y: 2. },
            Point { x: 2., y: 8. },
        ];
        let image = crop(&source, &triangle, true).unwrap();
        assert_eq!(image.dimensions(), (6, 6));
        assert_eq!(*image.get_pixel(0, 0), Rgba([5, 10, 15, 255]));
        assert_eq!(*image.get_pixel(5, 5), Rgba([0, 0, 0, 0]));
    }

    #[test]
    fn latest_screenshot_round_trips_and_jpeg_can_replace_a_saved_image() {
        let fixture = tempfile::tempdir().unwrap();
        let mut source = RgbaImage::from_pixel(32, 24, Rgba([255, 0, 0, 0]));
        remember(&source, fixture.path()).unwrap();
        assert_eq!(load_last(fixture.path()).unwrap(), source);
        let path = fixture.path().join("fixture.jpg");
        write_jpeg(&source, &path).unwrap();
        let white = image::open(&path).unwrap().into_rgb8().get_pixel(0, 0).0;
        assert!(white.iter().all(|channel| *channel >= 253));
        source.fill(255);
        source.put_pixel(1, 1, Rgba([23, 45, 67, 255]));
        remember(&source, fixture.path()).unwrap();
        assert_eq!(load_last(fixture.path()).unwrap(), source);
        write_jpeg(&source, &path).unwrap();
        assert_eq!(
            image::open(&path).unwrap().into_rgb8().dimensions(),
            (32, 24)
        );
    }

    #[cfg(windows)]
    #[test]
    fn clearing_history_removes_current_and_legacy_last_capture() {
        let fixture = tempfile::tempdir().unwrap();
        let source = RgbaImage::from_pixel(3, 3, Rgba([23, 45, 67, 255]));
        remember(&source, fixture.path()).unwrap();
        source
            .save(fixture.path().join("latest-screenshot.png"))
            .unwrap();
        let history = crate::windows::history::History::open(fixture.path()).unwrap();
        history.clear_unpinned(fixture.path()).unwrap();
        assert_eq!(
            load_last(fixture.path()).unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
        history.clear_unpinned(fixture.path()).unwrap();
    }
}
