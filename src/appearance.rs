use eframe::egui::{self, Color32, FontId, Stroke};
use std::sync::{Arc, LazyLock};

pub fn icon() -> Arc<egui::IconData> {
    static ICON: LazyLock<Arc<egui::IconData>> = LazyLock::new(|| {
        let image = image::load_from_memory_with_format(
            include_bytes!("../assets/Typlo.ico"),
            image::ImageFormat::Ico,
        )
        .expect("valid Typlo icon")
        .into_rgba8();
        Arc::new(egui::IconData {
            width: image.width(),
            height: image.height(),
            rgba: image.into_raw(),
        })
    });
    Arc::clone(&ICON)
}

pub fn window() -> egui::ViewportBuilder {
    egui::ViewportBuilder::default().with_icon(icon())
}

pub fn text_input(text: &mut dyn egui::TextBuffer) -> egui::TextEdit<'_> {
    egui::TextEdit::singleline(text)
        .margin(egui::Margin::symmetric(10, 7))
        .min_size(egui::vec2(0., 32.))
}

pub fn text_area(text: &mut dyn egui::TextBuffer) -> egui::TextEdit<'_> {
    egui::TextEdit::multiline(text).margin(egui::Margin::symmetric(10, 7))
}

pub fn configure(ctx: &egui::Context) {
    use egui::{FontFamily::Proportional, TextStyle};
    for (theme, panel, input, hover, button, border, accent) in [
        (
            egui::Theme::Light,
            0xfafafc,
            0xffffff,
            0xf2f2f4,
            0xffffff,
            0xd5d5d9,
            0x2563eb,
        ),
        (
            egui::Theme::Dark,
            0x202020,
            0x292929,
            0x383838,
            0x303030,
            0x484848,
            0x7da8ff,
        ),
    ] {
        let mut style = egui::Style::default();
        let visuals = &mut style.visuals;
        *visuals = theme.default_visuals();
        let (text, muted) = match theme {
            egui::Theme::Light => (0x242424, 0x606068),
            egui::Theme::Dark => (0xededed, 0xafafb5),
        };
        visuals.panel_fill = rgb(panel);
        visuals.window_fill = rgb(panel);
        visuals.extreme_bg_color = rgb(input);
        visuals.weak_text_color = Some(rgb(muted));
        visuals.widgets.noninteractive.fg_stroke.color = rgb(text);
        visuals.window_stroke = Stroke::new(1_f32, rgb(border));
        visuals.selection.bg_fill = rgb(accent).gamma_multiply(0.28);
        visuals.selection.stroke = Stroke::new(1_f32, rgb(accent));
        for widget in [
            &mut visuals.widgets.inactive,
            &mut visuals.widgets.hovered,
            &mut visuals.widgets.active,
        ] {
            widget.bg_stroke = Stroke::new(1_f32, rgb(border));
            widget.corner_radius = 4.into();
            widget.fg_stroke.color = rgb(text);
        }
        visuals.widgets.inactive.bg_fill = rgb(button);
        visuals.widgets.inactive.weak_bg_fill = rgb(button);
        visuals.widgets.hovered.bg_fill = rgb(hover);
        visuals.widgets.hovered.weak_bg_fill = rgb(hover);
        style.spacing.interact_size.y = 32.;
        style.spacing.item_spacing = egui::vec2(8., 6.);
        style.spacing.button_padding = egui::vec2(10., 4.);
        style
            .text_styles
            .insert(TextStyle::Body, FontId::new(14., Proportional));
        style
            .text_styles
            .insert(TextStyle::Button, FontId::new(14., Proportional));
        style
            .text_styles
            .insert(TextStyle::Heading, FontId::new(16., Proportional));
        ctx.set_style_of(theme, style);
    }
}

pub fn rgb(value: u32) -> Color32 {
    Color32::from_rgb((value >> 16) as u8, (value >> 8) as u8, value as u8)
}
