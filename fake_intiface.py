"""Stand-in for Intiface Central: speaks just enough Buttplug v3 to exercise the game's built-in
client, and logs every frame it receives (including whether it was a TEXT or BINARY frame) to
fake_intiface.log.   python fake_intiface.py [port]"""
import asyncio
import json
import sys
import time
from pathlib import Path

import websockets

LOG = Path(__file__).with_name("fake_intiface.log")
DEVICE = {
    "DeviceIndex": 0,
    "DeviceName": "Hismith Test Machine",
    "DeviceMessages": {
        "ScalarCmd": [{"StepCount": 100, "FeatureDescriptor": "Oscillator", "ActuatorType": "Oscillate"}],
        "StopDeviceCmd": {},
    },
}
T0 = time.time()


def log(line: str) -> None:
    with LOG.open("a", encoding="utf-8") as f:
        f.write(f"{time.time() - T0:8.3f} {line}\n")


async def handle(ws):
    log(f"CONNECT path={getattr(getattr(ws, 'request', None), 'path', '?')}")
    try:
        async for frame in ws:
            kind = "TEXT" if isinstance(frame, str) else "BINARY"
            text = frame if isinstance(frame, str) else frame.decode("utf-8", "replace")
            log(f"RECV {kind} {text}")
            try:
                msgs = json.loads(text)
            except json.JSONDecodeError as ex:
                log(f"BAD JSON: {ex}")
                continue
            replies = []
            for msg in msgs:
                (name, body), = msg.items()
                mid = body.get("Id", 0)
                if name == "RequestServerInfo":
                    replies.append({"ServerInfo": {"Id": mid, "ServerName": "Fake Intiface", "MessageVersion": 3, "MaxPingTime": int(sys.argv[2]) if len(sys.argv) > 2 else 0}})
                elif name == "RequestDeviceList":
                    replies.append({"DeviceList": {"Id": mid, "Devices": [DEVICE]}})
                else:
                    replies.append({"Ok": {"Id": mid}})
            await ws.send(json.dumps(replies))
    except websockets.ConnectionClosed as ex:
        log(f"CLOSED {ex}")
    log("DISCONNECT")


async def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 12345
    LOG.write_text("", encoding="utf-8")
    async with websockets.serve(handle, "127.0.0.1", port):
        log(f"LISTENING {port}")
        await asyncio.Future()

asyncio.run(main())
