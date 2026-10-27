import Gio from 'gi://Gio';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as Keyboard from 'resource:///org/gnome/shell/ui/status/keyboard.js';
import {createBridge} from './bridge.js';

const interfaceXml = `<node>
  <interface name="io.github.bitcodepro.Typlo.InputSources">
    <method name="Ping"><arg type="b" direction="out"/></method>
    <method name="GetCurrent"><arg type="s" direction="out"/></method>
    <method name="Activate"><arg type="s" direction="in"/><arg type="b" direction="out"/></method>
  </interface>
</node>`;

export default class TyploInputSources extends Extension {
    enable() {
        const bridge = createBridge(Keyboard.getInputSourceManager(), () => Main.sessionMode.isLocked);
        this._object = Gio.DBusExportedObject.wrapJSObject(interfaceXml, bridge);
        this._object.export(Gio.DBus.session, '/io/github/bitcodepro/Typlo/InputSources');
    }

    disable() {
        this._object?.unexport();
        this._object = null;
    }
}
