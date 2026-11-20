// Altos CHAT 3.1 (Hans Korneder, DataVision, Munich, 1987) as a scripted X.25 host.
//
// The same program as RetroIO's C# AltosChatEngine, written against the scripted-host API only (machine.*, the
// session object, onConnect / onInput / onDisconnect). It is the test of that API: a test runs this script and
// the C# engine side by side on the same calls and input and requires the same bytes, clears and log lines.
//
// Every rule, text and order comes from the C# engine, which follows the original C source
// (https://archive.org/details/altoschat). "serv.c:123" / "chat.c:123" below name lines of that source, as the
// C# comments do. Bugs a, b, d of the original are fixed and e guarded, bug c kept, exactly as in the C# engine.
//
// Settings (machine.settings, all strings): MaxUsers (30), OutputLimitBytes (4096), StartingNickname (guest),
// WaitPauseMs (3000), SiteLine (none).
//
// SiteLine is not in the original: one line printed before the 1987 welcome text, to say which machine answered
// (e.g. "Altos Chat System, Hamburg" when a second chat runs on the Hamburg number beside the Munich one). The
// welcome text itself stays the original's - its "Muenchen" is where the author worked, not where a chat stood.
// Unset (the default), the script sends exactly what the C# engine sends.

'use strict';

var TEXTLEN = 118;                                   // chat.h:28
var BELL_TEXT = '+++ requesting for a chat +++\n';   // /usr/lib/chat_msg
var ALL_CHANNELS = -1;                               // write_msg's "every channel" (serv.c:198)

var cfg = machine.settings;
var maxUsers = intSetting('MaxUsers', 30);
var outputLimit = intSetting('OutputLimitBytes', 4096);
var startingNickname = cfg.StartingNickname === undefined ? 'guest' : cfg.StartingNickname;
var waitPauseMs = intSetting('WaitPauseMs', 3000);
var siteLine = cfg.SiteLine === undefined ? '' : String(cfg.SiteLine);

if (maxUsers < 1) {
    throw new Error('MaxUsers must be at least 1.');
}

// The users[] and chans[] tables of the server (serv.c:18-33).
var users = [];
var chans = [];
for (var n = 0; n < maxUsers; n++) {
    users.push({ inUse: false, owner: null, login: '', nickname: '', channel: 0, from: '' });
    chans.push({ chan: -1, pass: '' });   // serv.c:169-171
}

// Every call in the order it came, and the same by session id.
var sessions = [];
var byId = {};

// ------------------------------------------------------------------ events from the host

function onStart() {
    toLog('Chat-Server Starting At: ' + ctime(machine.now()));   // serv.c:166-167
}

function onConnect(s) {
    var st = {
        s: s,
        id: s.id,
        state: 'waiting',
        slot: -1,
        // chat.c:312-314, chat.c:131: cut to 8, filter, padded to 8.
        login: pad(filter(cut(s.login, 8)), 8),
        from: pad(fromRule(s.calling), 16),
        connectedAt: machine.now(),
        line: '',
        early: ''
    };
    sessions.push(st);
    byId[s.id] = st;

    // Not in the original: the SiteLine setting, when given (see the top of this file).
    if (siteLine.length > 0) {
        msg(st, siteLine + '\n');
    }

    // chat.c:248-250, then chat.c:277.
    msg(st, 'Welcome to the original, evergreen\n');
    msg(st, 'Conference-utility "CHAT"  ver. 3.1\n');
    msg(st, 'Copyright (C) 1987 Hans Korneder, Muenchen\n');
    msg(st, 'Waiting for connection.\n');

    if (waitPauseMs <= 0) {
        joinNow(st);
    } else {
        machine.after(waitPauseMs, function () {
            if (st.state === 'waiting') {
                joinNow(st);
            }
        });
    }
}

function onInput(s, text) {
    var st = byId[s.id];
    if (st === undefined) {
        return;
    }

    input(st, text);
}

function onDisconnect(s) {
    var st = byId[s.id];
    if (st === undefined) {
        return;
    }

    leave(st, false);   // the SIGHUP log_off (chat.c:246, 101-125); no clear is sent back
}

// ------------------------------------------------------------------ the per-user side (chat.c)

function input(st, text) {
    if (st.state === 'waiting') {
        st.early += text;   // waited in the terminal buffer during pause() (chat.c:278)
        return;
    }

    for (var i = 0; i < text.length; i++) {
        // get_line, chat.c:161-170, one byte at a time. CR ends a line too (the PAD forwards on CR).
        var c = text.charCodeAt(i) & 0x7F;
        var end = c === 10 || c === 13;
        if (!end) {
            if (c < 32 || c > 126) {
                continue;
            }

            st.line += String.fromCharCode(c);
            if (st.line.length < TEXTLEN) {
                continue;
            }
        }

        var line = st.line;
        st.line = '';
        if (!handleLine(st, line)) {
            return;   // the user left: the rest of the input is never read
        }
    }
}

function joinNow(st) {
    // The lowest free place (serv.c:223-224).
    var i = 0;
    while (i < users.length && users[i].inUse) {
        i++;
    }

    if (i === users.length) {
        msg(st, 'open of answerpipe failed.\n');   // serv.c:225-229, chat.c:261-269
        clearCall(st);
        return;
    }

    st.state = 'joined';
    st.slot = i;
    msg(st, 'Connection established.\nGet help with:    .h\n');   // chat.c:262-270

    var u = users[i];
    u.inUse = true;
    u.owner = st;
    u.login = st.login;
    u.from = st.from;
    u.channel = 0;
    u.nickname = trimEndSpaces(filter(cut(startingNickname, 32)));   // chat.c:317, 131; serv.c:234, 237

    toLog('Incoming Call From ' + u.from + ' At ' + ctime(machine.now()));   // serv.c:250-252
    var text = '+++' + u.nickname + '+++\n';                                  // serv.c:253
    toLogText(text);
    for (var k = 0; k < users.length; k++) {
        writeMsg(k, ALL_CHANNELS, text);
    }

    if (st.early.length > 0 && st.state === 'joined') {
        var early = st.early;
        st.early = '';
        input(st, early);
    }
}

// One line: chat.c's process() (chat.c:173-239), then the server's (serv.c:266-383). Returns false when gone.
function handleLine(st, line) {
    if (startsWith(line, '.w')) {
        whoList(st);
        msg(st, '--- back in chat ---\n');
        return true;
    }

    if (startsWith(line, '.m')) {
        msg(st, 'This is disabled by default- use SMTP!\n');   // chat.c:187-189
        return true;
    }

    if (startsWith(line, '.b')) {
        // wall < /usr/lib/chat_msg (chat.c:199-203): every call on this machine, the sender's too.
        var all = sessions.slice();
        for (var k = 0; k < all.length; k++) {
            msg(all[k], BELL_TEXT);
        }

        msg(st, '--- back in chat ---\n');
        return true;
    }

    if (startsWith(line, '.h')) {
        help(st);   // chat.c:205-209
        return true;
    }

    if (startsWith(line, '.q') || startsWith(line, '.x')) {
        leave(st, true);   // chat.c:210-213, 283
        return false;
    }

    if (startsWith(line, '--!')) {
        msg(st, 'Function is being disabled by default\n');   // chat.c:214-217
        return true;
    }

    if (startsWith(line, '.so')) {
        msg(st, 'Function is being disabled by default\n');   // chat.c:224-228
        return true;
    }

    if (st.state !== 'joined') {
        return true;   // thrown out as slow: the server ignores the packet (serv.c:267-272)
    }

    serverText(st.slot, line);
    return true;
}

function leave(st, clear) {
    if (st.state === 'joined') {
        logOff(st.slot);
    }

    if (clear) {
        clearCall(st);
    } else {
        forget(st);
    }
}

function clearCall(st) {
    forget(st);
    st.s.clear();
}

function forget(st) {
    st.state = 'gone';
    var at = sessions.indexOf(st);
    if (at >= 0) {
        sessions.splice(at, 1);
    }

    delete byId[st.id];
}

function help(st) {
    msg(st, '.x           exit this program\n');
    msg(st, '.s           get the list of users on the chat\n');
    msg(st, '.n name      change your nickname\n');
    msg(st, '.h           get this page of text\n');
    msg(st, '.w           get a list of users on the system\n');
    msg(st, '.b           bell all users\n');
    msg(st, '.m           send mail to the sysop\n');
    msg(st, '.c #         switch to channel no <#>\n');
    msg(st, '.p passwd    set a passwd for a channel\n');
    msg(st, '!# text      send a private message to user <#>\n');
}

// .w: one row per call on this machine, "%-8.8s %-12.12s %.12s" in the manner of who(1).
function whoList(st) {
    for (var k = 0; k < sessions.length; k++) {
        var o = sessions[k];
        var from = cut(o.from, 12);
        var when = ctime(o.connectedAt).substring(4, 16);
        msg(st, o.login + ' ' + pad(from, 12) + ' ' + when + '\n');
    }
}

// ------------------------------------------------------------------ the server side (serv.c)

function serverText(i, line) {
    // serv.c:273 blankstrip: only a full 118-character line loses its trailing spaces (bug c kept, bug b fixed).
    if (line.length === TEXTLEN) {
        var e = line.length;
        while (e > 0 && line.charAt(e - 1) === ' ') {
            e--;
        }

        line = line.substring(0, e);
    }

    if (line.length === 0) {
        return;   // serv.c:274
    }

    var me = users[i];
    var text = line;
    var k;

    if (startsWith(line, '.n ')) {
        // serv.c:276-285: the announcement shows the whole new name; the name kept is cut to 32.
        var m = '>>>' + me.nickname + '>>>' + text.substring(3) + '>>>\n';
        toLogText(m);
        var chan = me.channel;
        for (k = 0; k < users.length; k++) {
            writeMsg(k, chan, m);
        }

        if (me.inUse) {
            me.nickname = cut(text.substring(3), 32);
        }

        return;
    }

    if (startsWith(line, '.s')) {
        // serv.c:286-301: every user on every channel, by place. "%2d %10ld %s %s %s\n".
        writeMsg(i, ALL_CHANNELS, 'No Chan       From             User     Called\n');
        for (k = 0; k < users.length; k++) {
            var u = users[k];
            if (u.inUse) {
                writeMsg(i, ALL_CHANNELS, padLeft(String(k), 2) + ' ' + padLeft(String(u.channel), 10) + ' ' +
                    u.from + ' ' + u.login + ' ' + u.nickname + '\n');
            }
        }

        return;
    }

    if (startsWith(line, '.p ')) {
        protect(i, text);
        return;
    }

    if (startsWith(line, '.c')) {
        changeChannel(i, text);
        return;
    }

    if (startsWith(line, '!')) {
        // serv.c:368-376: to place N on any channel, the whole line quoted; a bad place is silent.
        // Not 1987 (decided 2026-11-18): no number after '!' made atol give 0, so "!name text" went to place 0,
        // often the sender. Now the sender is told and nothing is sent. Same text as AltosChatEngine.
        if (!startsWithNumber(line.substring(1))) {
            writeMsg(i, ALL_CHANNELS, 'Whisper with the user number from .s: !# text\n');
            return;
        }

        var target = atol(line.substring(1));
        var w = me.nickname + ' whispers: ' + text + '\n';
        toLog('[' + i + ':' + me.channel + ']' + w.substring(0, w.length - 1));
        if (target >= 0 && target < users.length) {
            writeMsg(target, ALL_CHANNELS, w);
        }

        return;
    }

    // serv.c:377-383: to the others on the same channel, not to the sender (bug a fixed: a local string).
    var says = me.nickname + ' says: ' + text + '\n';
    toLog('[' + i + ':' + me.channel + ']' + says.substring(0, says.length - 1));
    var ch = me.channel;
    for (k = 0; k < users.length; k++) {
        if (k !== i) {
            writeMsg(k, ch, says);
        }
    }
}

// .p passwd, serv.c:302-332.
function protect(i, text) {
    var me = users[i];
    if (me.channel === 0) {
        writeMsg(i, ALL_CHANNELS, 'Channel 0 cannot be protected.\n');
        return;
    }

    var count = 0;
    var k;
    for (k = 0; k < users.length; k++) {
        if (users[k].inUse && users[k].channel === me.channel) {
            count++;
        }
    }

    if (count > 1) {
        writeMsg(i, ALL_CHANNELS, 'You are not alone on the channel.\n');
        return;
    }

    var e = 0;
    while (e < chans.length && chans[e].chan !== me.channel) {
        e++;
    }

    if (e === chans.length) {
        e = 0;
        while (e < chans.length && chans[e].chan >= 0) {
            e++;
        }
    }

    if (e === chans.length) {
        return;   // bug e guarded: cannot happen (see the C# engine)
    }

    chans[e].chan = me.channel;
    chans[e].pass = cut(text.substring(3), 8);   // strncpy 8 (serv.c:328)
}

// .c N[,passwd], serv.c:333-367.
function changeChannel(i, text) {
    var me = users[i];
    var old = me.channel;
    var chan = atol(text.substring(2));

    // serv.c:338 "if (new<0) new = -new;" (bug d fixed: done outside 32 bits, as the C# engine does in 64).
    if (chan < 0) {
        chan = -chan;
    }

    if (old === chan) {
        return;   // serv.c:339
    }

    var e = 0;
    while (e < chans.length && chans[e].chan !== chan) {
        e++;
    }

    if (e < chans.length) {
        var comma = text.indexOf(',');
        if (comma < 0) {
            writeMsg(i, ALL_CHANNELS, 'Channel is protected. Use a passwd\n');
            return;
        }

        if (!strNCmpEqual(chans[e].pass, text.substring(comma + 1), 8)) {
            writeMsg(i, ALL_CHANNELS, 'Wrong passwd.\n');
            return;
        }
    }

    var m = '>>>' + me.nickname + '>>> chan ' + chan + '\n';
    for (var k = 0; k < users.length; k++) {
        writeMsg(k, old, m);
        writeMsg(k, chan, m);
    }

    toLogText(m);
    if (me.inUse) {
        me.channel = chan;
    }

    checkFree(old);
}

// A user leaves the chat, serv.c:119-131. The place is freed first, so the leaver is not told.
function logOff(i) {
    var u = users[i];
    u.inUse = false;
    var owner = u.owner;
    u.owner = null;
    if (owner !== null && owner.state === 'joined') {
        owner.state = 'deaf';
        owner.slot = -1;
    }

    var text = '---' + u.nickname + '---\n';
    toLogText(text);
    for (var k = 0; k < users.length; k++) {
        writeMsg(k, ALL_CHANNELS, text);
    }

    checkFree(u.channel);
}

// A channel with nobody left loses its password, serv.c:107-117.
function checkFree(ch) {
    var k;
    for (k = 0; k < users.length; k++) {
        if (users[k].inUse && users[k].channel === ch) {
            return;
        }
    }

    for (k = 0; k < chans.length; k++) {
        if (chans[k].chan === ch) {
            chans[k].chan = -1;
        }
    }
}

// The server writes to one user, serv.c:188-205. A write over the output limit throws the user out (log_off).
function writeMsg(person, channel, text) {
    if (person < 0 || person >= users.length) {
        return;
    }

    var u = users[person];
    if (!u.inUse) {
        return;
    }

    if (channel >= 0 && channel !== u.channel) {
        return;
    }

    var st = u.owner;
    if (st.s.pending() + machine.wireLength(text) > outputLimit) {
        logOff(person);
        return;
    }

    msg(st, text);
}

// ------------------------------------------------------------------ output, log, helpers

function msg(st, text) {
    st.s.send(text);
}

function toLog(line) {
    machine.log(line);
}

function toLogText(text) {
    toLog(text.substring(0, text.length - 1));
}

var DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// ctime() without its "\n", "%.24s": "Fri May 29 12:00:00 1987", the day of the month padded with a space.
function ctime(t) {
    return DAYS[t.weekday] + ' ' + MONTHS[t.month - 1] + ' ' + padLeft(String(t.day), 2) + ' ' +
        two(t.hour) + ':' + two(t.minute) + ':' + two(t.second) + ' ' + t.year;
}

function two(v) {
    return v < 10 ? '0' + v : String(v);
}

// True when atol would read a number: spaces, at most one sign, then a digit.
function startsWithNumber(s) {
    var p = 0;
    while (p < s.length && s.charAt(p) === ' ') {
        p++;
    }

    if (p < s.length && (s.charAt(p) === '-' || s.charAt(p) === '+')) {
        p++;
    }

    return p < s.length && s.charAt(p) >= '0' && s.charAt(p) <= '9';
}

// atol() of the 1987 C library: spaces skipped, one sign, digits in a 32-bit long that wraps.
function atol(s) {
    var p = 0;
    while (p < s.length && s.charAt(p) === ' ') {
        p++;
    }

    var neg = false;
    if (p < s.length && (s.charAt(p) === '-' || s.charAt(p) === '+')) {
        neg = s.charAt(p) === '-';
        p++;
    }

    var v = 0;
    while (p < s.length && s.charAt(p) >= '0' && s.charAt(p) <= '9') {
        v = (Math.imul(v, 10) + (s.charCodeAt(p) - 48)) | 0;
        p++;
    }

    return neg ? ((-v) | 0) : v;   // 32-bit: -(-2147483648) stays -2147483648, as in C
}

// strncmp(a, b, n) == 0 for two C strings.
function strNCmpEqual(a, b, count) {
    for (var i = 0; i < count; i++) {
        var ca = i < a.length ? a.charCodeAt(i) : 0;
        var cb = i < b.length ? b.charCodeAt(i) : 0;
        if (ca !== cb) {
            return false;
        }

        if (ca === 0) {
            return true;
        }
    }

    return true;
}

function startsWith(line, prefix) {
    return line.length >= prefix.length && line.substring(0, prefix.length) === prefix;
}

// filter(), chat.c:73-85: top bit off, control characters and DEL out.
function filter(text) {
    var out = '';
    for (var i = 0; i < text.length; i++) {
        var c = text.charCodeAt(i) & 0x7F;
        if (c < 32 || c > 126) {
            continue;
        }

        out += String.fromCharCode(c);
    }

    return out;
}

// Where the caller comes from, chat.c:325-329.
function fromRule(from) {
    var f;
    if (from === null || from === undefined) {
        f = 'somewhere';
    } else {
        f = cut(from, 16);
        if (f.length > 0 && f.charAt(0) === ':') {
            f = 'Xlocalhost';
        }

        if (f.length === 0) {
            f = 'localhost';
        }
    }

    return filter(f);
}

function cut(text, max) {
    return text.length > max ? text.substring(0, max) : text;
}

function pad(text, width) {
    var t = cut(text, width);
    while (t.length < width) {
        t += ' ';
    }

    return t;
}

function padLeft(text, width) {
    var t = text;
    while (t.length < width) {
        t = ' ' + t;
    }

    return t;
}

function trimEndSpaces(text) {
    var e = text.length;
    while (e > 0 && text.charAt(e - 1) === ' ') {
        e--;
    }

    return text.substring(0, e);
}

function intSetting(name, fallback) {
    var v = cfg[name];
    if (v === undefined || v === '') {
        return fallback;
    }

    var parsed = parseInt(v, 10);
    if (isNaN(parsed)) {
        throw new Error('setting ' + name + ' is not a number: ' + v);
    }

    return parsed;
}
