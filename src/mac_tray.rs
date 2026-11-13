use crate::{locale::text as tr, unix_desktop::TrayAction};
use objc2::{
    DefinedClass, MainThreadMarker, MainThreadOnly, define_class, msg_send,
    rc::Retained,
    runtime::{AnyObject, Sel},
    sel,
};
use objc2_app_kit::{
    NSApplication, NSApplicationActivationPolicy, NSEvent, NSEventModifierFlags, NSEventType,
    NSMenu, NSMenuItem, NSStatusBar, NSStatusItem,
};
use objc2_foundation::{NSObject, NSPoint, NSString, ns_string};
use std::{
    cell::RefCell,
    io,
    path::{Path, PathBuf},
    process::{Child, Command},
    sync::{Arc, Mutex},
};

pub(super) type HistoryWindow = Arc<Mutex<Option<Child>>>;

pub(super) fn request_stop() {
    if let Some(main) = MainThreadMarker::new() {
        let app = NSApplication::sharedApplication(main);
        app.stop(None);
        // The tap consumes the stop signal. AppKit checks its stop flag after an app event.
        let wake = NSEvent::otherEventWithType_location_modifierFlags_timestamp_windowNumber_context_subtype_data1_data2(
            NSEventType::ApplicationDefined,
            NSPoint::new(0.0, 0.0),
            NSEventModifierFlags::empty(),
            0.0,
            0,
            None,
            0,
            0,
            0,
        );
        if let Some(wake) = wake {
            app.postEvent_atStart(&wake, true);
        }
    }
}

struct MenuState {
    profile: PathBuf,
    settings: RefCell<Option<Child>>,
    history: HistoryWindow,
}

define_class!(
    #[unsafe(super = NSObject)]
    #[name = "TyploRustMenuActions"]
    #[thread_kind = MainThreadOnly]
    #[ivars = MenuState]
    struct MenuActions;

    impl MenuActions {
        #[unsafe(method(openTyploSettings:))]
        fn open_settings(&self, _: Option<&AnyObject>) {
            let state = self.ivars();
            let mut settings = state.settings.borrow_mut();
            if settings.as_mut().is_none_or(|child| !matches!(child.try_wait(), Ok(None))) {
                match crate::jobs::gui().and_then(|exe| Command::new(exe).arg("--profile").arg(&state.profile).spawn()) {
                    Ok(child) => *settings = Some(child),
                    Err(error) => eprintln!("Typlo settings: {error}"),
                }
            }
        }

        #[unsafe(method(openTyploHistory:))]
        fn open_history(&self, _: Option<&AnyObject>) {
            let state = self.ivars();
            if let Ok(mut history) = state.history.lock()
                && let Err(error) = crate::unix_clipboard::history_action_with_origin(&state.profile, &mut history, crate::macos::history_origin())
            {
                eprintln!("Typlo clipboard: {error}");
            }
        }

        #[unsafe(method(runTyploMenuAction:))]
        fn run_action(&self, sender: &NSMenuItem) {
            if let Some(action) = usize::try_from(sender.tag()).ok().and_then(|tag| TrayAction::ALL.get(tag))
                && let Err(error) = action.run(&self.ivars().profile)
            {
                eprintln!("Typlo tray: {error}");
            }
        }
    }
);

pub(super) struct Tray {
    app: Retained<NSApplication>,
    bar: Retained<NSStatusBar>,
    item: Retained<NSStatusItem>,
    _actions: Retained<MenuActions>,
}

impl Tray {
    pub fn new(profile: &Path, history: HistoryWindow) -> io::Result<Self> {
        let main = MainThreadMarker::new()
            .ok_or_else(|| io::Error::other("The macOS tray must run on the main thread"))?;
        let app = NSApplication::sharedApplication(main);
        crate::locale::load(profile);
        app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);
        let bar = NSStatusBar::systemStatusBar();
        let item = bar.statusItemWithLength(-1.0);
        if let Some(button) = item.button(main) {
            button.setTitle(ns_string!("Typlo"));
        }
        let state = MenuState {
            profile: profile.to_owned(),
            settings: RefCell::default(),
            history,
        };
        let actions: Retained<MenuActions> =
            unsafe { msg_send![super(MenuActions::alloc(main).set_ivars(state)), init] };
        let menu = NSMenu::new(main);
        let entry = |title: &str, selector: Sel, target: &AnyObject| unsafe {
            let item = NSMenuItem::initWithTitle_action_keyEquivalent(
                NSMenuItem::alloc(main),
                &NSString::from_str(title),
                Some(selector),
                ns_string!(""),
            );
            item.setTarget(Some(target));
            item
        };
        menu.addItem(&entry(
            tr("tray.settings", "Settings"),
            sel!(openTyploSettings:),
            &actions,
        ));
        menu.addItem(&entry(
            tr("tray.history", "Clipboard history"),
            sel!(openTyploHistory:),
            &actions,
        ));
        for (index, action) in TrayAction::ALL.into_iter().enumerate() {
            let item = entry(action.label(), sel!(runTyploMenuAction:), &actions);
            item.setTag(index as isize);
            menu.addItem(&item);
        }
        menu.addItem(&entry(tr("tray.exit", "Exit"), sel!(stop:), &app));
        item.setMenu(Some(&menu));
        Ok(Self {
            app,
            bar,
            item,
            _actions: actions,
        })
    }

    pub fn run(&self) {
        self.app.run();
    }
}

impl Drop for Tray {
    fn drop(&mut self) {
        self.bar.removeStatusItem(&self.item);
    }
}
