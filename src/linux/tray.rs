use crate::{locale::text as tr, unix_desktop::TrayAction};
use ksni::{MenuItem, blocking::TrayMethods, menu::StandardItem};
use std::{
    io,
    path::{Path, PathBuf},
    process::Child,
    sync::mpsc::{self, Receiver, SyncSender},
};

#[derive(Clone, Copy)]
enum Command {
    Settings,
    History,
    Extra(TrayAction),
    Quit,
}

struct Icon {
    commands: SyncSender<Command>,
    language: String,
}

impl ksni::Tray for Icon {
    fn id(&self) -> String {
        "typlo-rust".into()
    }
    fn title(&self) -> String {
        "Typlo2.0".into()
    }
    fn icon_name(&self) -> String {
        if cfg!(feature = "gui") {
            String::new()
        } else {
            "input-keyboard".into()
        }
    }

    #[cfg(feature = "gui")]
    fn icon_pixmap(&self) -> Vec<ksni::Icon> {
        let icon = crate::appearance::icon();
        let data = icon
            .rgba
            .as_chunks::<4>()
            .0
            .iter()
            .flat_map(|&[r, g, b, a]| [a, r, g, b])
            .collect();
        vec![ksni::Icon {
            width: icon.width as i32,
            height: icon.height as i32,
            data,
        }]
    }

    fn activate(&mut self, _x: i32, _y: i32) {
        let _ = self.commands.try_send(Command::Settings);
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        crate::locale::set_language(&self.language);
        [
            (tr("tray.settings", "Settings"), Command::Settings),
            (tr("tray.history", "Clipboard history"), Command::History),
        ]
        .into_iter()
        .chain(
            TrayAction::ALL
                .into_iter()
                .map(|action| (action.label(), Command::Extra(action))),
        )
        .chain([(tr("tray.exit", "Exit"), Command::Quit)])
        .map(|(label, command)| {
            StandardItem {
                label: label.into(),
                activate: Box::new(move |tray: &mut Self| {
                    let _ = tray.commands.try_send(command);
                }),
                ..Default::default()
            }
            .into()
        })
        .collect()
    }
}

pub(super) struct Tray {
    _handle: Option<ksni::blocking::Handle<Icon>>,
    profile: PathBuf,
    actions: Receiver<Command>,
    settings: Option<Child>,
    history: Option<Child>,
}

impl Tray {
    pub fn new(profile: &Path) -> Self {
        let language = crate::locale::load(profile).ui_language;
        let (sender, actions) = mpsc::sync_channel(4);
        let handle = Icon {
            commands: sender,
            language,
        }
        .spawn()
        .ok();
        Self {
            _handle: handle,
            profile: profile.to_owned(),
            actions,
            settings: None,
            history: None,
        }
    }

    pub fn poll(&mut self) -> io::Result<bool> {
        for action in self.actions.try_iter() {
            match action {
                Command::Settings => {
                    if self
                        .settings
                        .as_mut()
                        .is_none_or(|child| !matches!(child.try_wait(), Ok(None)))
                    {
                        self.settings = Some(super::open_settings(&self.profile)?);
                    }
                }
                Command::History => {
                    crate::unix_clipboard::history_action(&self.profile, &mut self.history)?
                }
                Command::Extra(action) => {
                    if let Err(error) = action.run(&self.profile) {
                        eprintln!("Typlo tray: {error}");
                    }
                }
                Command::Quit => return Ok(true),
            }
        }
        Ok(false)
    }
}
