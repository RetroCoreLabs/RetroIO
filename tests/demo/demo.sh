#!/usr/bin/env bash
#
# X.25 C# demo network: three chained switches (A -> B -> C) with distinct DNICs
# plus an echo service on Switch C. Mirrors the reference demo's UX.
#
#   Switch A  DNIC 2350  access 2200   mgmt 9101   monitor 4001
#   Switch B  DNIC 2351  trunk  2201   mgmt 9102   monitor 4002
#   Switch C  DNIC 2352  trunk  2202   mgmt 9103   monitor 4003
#   echo      answers on 23520001, connected to Switch C access port 2203
#
# The PAD connects to Switch A; CALL 23520001 routes A -> B -> C -> echo.
#
# Usage: ./demo/demo.sh {start|stop|status|pad|gui|cli-a|cli-b|cli-c}

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEMO="$ROOT/demo"
LOGS="$DEMO/logs"
PIDS="$DEMO/.pids"
CFG="net10.0"
BIN="bin/Debug/$CFG"

SWITCH="$ROOT/apps/X25Emulator.Switch/$BIN/x25switch.dll"
ECHO="$ROOT/apps/X25Emulator.Echo/$BIN/x25echo.dll"
PAD="$ROOT/apps/X25Emulator.Pad/$BIN/x25pad.dll"
GUI="$ROOT/apps/X25Emulator.MonitorGui/$BIN/X25Emulator.MonitorGui.dll"

build() {
    echo "Building..."
    dotnet build "$ROOT/X25Emulator.sln" -c Debug --nologo -v quiet || exit 1
}

launch() {
    # launch <logfile> <dll> [args...]
    local log="$1"; shift
    dotnet "$@" > "$LOGS/$log" 2>&1 &
    echo $! >> "$PIDS"
}

start() {
    if [ -f "$PIDS" ]; then
        echo "Demo already running (see $PIDS). Run './demo/demo.sh stop' first."
        exit 1
    fi
    build
    mkdir -p "$LOGS"
    : > "$PIDS"

    echo "Starting Switch C (DNIC=2352, mgmt=9103)..."
    launch switch-c.log "$SWITCH" "$DEMO/switch-c.ini"
    sleep 2

    echo "Starting Switch B (DNIC=2351, mgmt=9102)..."
    launch switch-b.log "$SWITCH" "$DEMO/switch-b.ini"
    sleep 2

    echo "Starting Switch A (DNIC=2350, mgmt=9101)..."
    launch switch-a.log "$SWITCH" "$DEMO/switch-a.ini"
    sleep 2

    echo "Starting echo service (address 23520001, via Switch C port 2203)..."
    launch echo.log "$ECHO" -x 127.0.0.1:2203 -a 23520001
    sleep 1

    cat <<'EOF'

Network ready!

To test:
  ./demo/demo.sh pad         - Launch PAD client (connects to Switch A)
  Then at the * prompt:      CALL 23520001
  Type anything - it echoes back!

  ./demo/demo.sh gui         - Launch the Avalonia monitor GUI
  In the GUI add/select a switch (127.0.0.1 : 4001/4002/4003) and Connect.

Management consoles (telnet):
  ./demo/demo.sh cli-a       - Switch A (telnet localhost 9101)
  ./demo/demo.sh cli-b       - Switch B (telnet localhost 9102)
  ./demo/demo.sh cli-c       - Switch C (telnet localhost 9103)

  ./demo/demo.sh stop        - Stop the whole network
EOF
}

stop() {
    if [ ! -f "$PIDS" ]; then
        echo "Demo not running."
        return
    fi
    echo "Stopping demo..."
    while read -r pid; do
        [ -n "$pid" ] && kill "$pid" 2>/dev/null
    done < "$PIDS"
    rm -f "$PIDS"
    echo "Stopped."
}

status() {
    if [ ! -f "$PIDS" ]; then
        echo "Demo not running."
        return
    fi
    echo "Demo PIDs:"
    cat "$PIDS"
}

case "${1:-}" in
    start)  start ;;
    stop)   stop ;;
    status) status ;;
    pad)    dotnet "$PAD" -x 127.0.0.1:2200 ;;
    gui)    dotnet "$GUI" ;;
    cli-a)  telnet localhost 9101 ;;
    cli-b)  telnet localhost 9102 ;;
    cli-c)  telnet localhost 9103 ;;
    *)      echo "Usage: ./demo/demo.sh {start|stop|status|pad|gui|cli-a|cli-b|cli-c}" ;;
esac
