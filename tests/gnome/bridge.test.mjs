import {readFile} from 'node:fs/promises';
import {test} from 'node:test';
import assert from 'node:assert/strict';

const source = await readFile(new URL('../../integrations/gnome/bridge.js', import.meta.url), 'utf8');
const {createBridge} = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture() {
    const manager = {inputSources: {}, currentSource: null};
    let locked = false;
    const activations = [];
    for (const [index, id] of ['us', 'ru', 'ua', 'fr+oss', 'de'].entries()) {
        const item = {type: 'xkb', id, activate(interactive) {
            activations.push([id, interactive]);
            manager.currentSource = item; // Shell also loads this source's group and updates per-window state.
        }};
        manager.inputSources[index] = item;
    }
    manager.currentSource = manager.inputSources[0];
    return {manager, activations, bridge: createBridge(manager, () => locked), lock: () => { locked = true; }};
}

test('five-source cycles activate through Shell in either direction', () => {
    const {bridge, activations} = fixture();
    for (const id of ['ru', 'ua', 'fr+oss', 'de', 'us', 'de', 'fr+oss']) {
        assert.equal(bridge.Activate(id), true);
        assert.equal(bridge.GetCurrent(), id);
    }
    assert.equal(activations.length, 7);
    assert.ok(activations.every(([, interactive]) => interactive));
});

test('unknown sources and the lock screen do not change the source', () => {
    const {bridge, activations, lock} = fixture();
    assert.equal(bridge.Ping(), true);
    assert.equal(bridge.Activate('unknown'), false);
    assert.equal(bridge.Activate("de'); unsafe()"), false);
    lock();
    assert.equal(bridge.Activate('de'), false);
    assert.equal(bridge.GetCurrent(), 'us');
    assert.deepEqual(activations, []);
});

test('source list changes and non-XKB sources are handled at request time', () => {
    const {bridge, manager} = fixture();
    delete manager.inputSources[4];
    assert.equal(bridge.Activate('de'), false);
    manager.inputSources[8] = {type: 'ibus', id: 'ime', activate() { assert.fail('must not activate IME'); }};
    assert.equal(bridge.Activate('ime'), false);
    manager.currentSource = manager.inputSources[8];
    assert.equal(bridge.GetCurrent(), '');
    manager.currentSource = null;
    assert.equal(bridge.GetCurrent(), '');
});
