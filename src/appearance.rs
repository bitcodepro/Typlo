use eframe::egui::{self, Color32, FontId, Stroke};
use std::sync::{Arc, LazyLock};

pub(crate) fn consume_shortcut_key(
    input: &mut egui::InputState,
    modifiers: egui::Modifiers,
    key: egui::Key,
) -> bool {
    let count = input.events.len();
    input.events.retain(|event| {
        !matches!(event, egui::Event::Key {
            key: logical,
            physical_key,
            modifiers: pressed,
            pressed: true,
            ..
        } if physical_key.unwrap_or(*logical) == key && pressed.matches_exact(modifiers))
    });
    count != input.events.len()
}

#[cfg(test)]
#[test]
fn shortcuts_match_physical_keys_with_exact_modifiers() {
    use egui::{Event, Key, Modifiers};
    let ctx = egui::Context::default();
    let input = egui::RawInput {
        events: vec![Event::Key {
            key: Key::Q,
            physical_key: Some(Key::O),
            pressed: true,
            repeat: false,
            modifiers: Modifiers::CTRL | Modifiers::SHIFT,
        }],
        ..Default::default()
    };
    let _ = ctx.run_ui(input, |ui| {
        ui.input_mut(|input| {
            assert!(!consume_shortcut_key(input, Modifiers::CTRL, Key::O));
            assert!(!consume_shortcut_key(
                input,
                Modifiers::CTRL | Modifiers::SHIFT,
                Key::Q
            ));
            assert!(consume_shortcut_key(
                input,
                Modifiers::CTRL | Modifiers::SHIFT,
                Key::O
            ));
        });
    });
}

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

/// Background of every Typlo window. Windows that draw straight into the root
/// `Ui` have no panel, so without this the theme's background is not painted.
pub fn clear_color(visuals: &egui::Visuals) -> [f32; 4] {
    visuals.panel_fill.to_normalized_gamma_f32()
}

/// Standard page body: themed background and the same margins as Settings.
pub fn page<R>(ui: &mut egui::Ui, add_contents: impl FnOnce(&mut egui::Ui) -> R) -> R {
    egui::Frame::new()
        .fill(ui.visuals().panel_fill)
        .inner_margin(egui::Margin::symmetric(20, 14))
        .show(ui, |ui| {
            ui.set_min_width(ui.available_width());
            add_contents(ui)
        })
        .inner
}

/// Standard footer with a separator line, used for the main window actions.
pub fn footer(ui: &mut egui::Ui, id: &'static str, add_contents: impl FnOnce(&mut egui::Ui)) {
    egui::Panel::bottom(id)
        .show_separator_line(true)
        .frame(egui::Frame::new().fill(ui.visuals().panel_fill))
        .show_inside(ui, |ui| {
            egui::Frame::new()
                .inner_margin(egui::Margin::symmetric(20, 12))
                .show(ui, |ui| ui.horizontal(add_contents));
        });
}

pub fn heading(ui: &mut egui::Ui, label: &str) {
    ui.add_space(6.);
    ui.label(egui::RichText::new(label).size(16.).strong());
}

pub fn hint(ui: &mut egui::Ui, text: &str) {
    ui.label(
        egui::RichText::new(text)
            .size(12.)
            .color(ui.visuals().weak_text_color()),
    );
}

/// Accent button for the main action of a window.
pub fn primary_button(label: &str) -> egui::Button<'_> {
    egui::Button::new(egui::RichText::new(label).color(Color32::WHITE)).fill(rgb(0x2563eb))
}

/// Bordered group for lists inside a page.
pub fn group(ui: &egui::Ui) -> egui::Frame {
    egui::Frame::new()
        .fill(ui.visuals().extreme_bg_color)
        .stroke(ui.visuals().window_stroke)
        .corner_radius(6)
        .inner_margin(egui::Margin::symmetric(12, 8))
}
