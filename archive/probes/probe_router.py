"""Probe Schicht 1 (TCP zu Router) und Schicht 2 (AMS-Routing zur PLC)."""
import asyncio
import sys
from ads_async.asyncio.client import Client, _BlockingRequest
from ads_async import exceptions as _ads_exc


# Python 3.13 compat patch: asyncio.wait disallows bare coroutines.
async def _patched_wait(self, timeout=2.0):
    event_task = asyncio.create_task(self._event.wait())
    error_task = asyncio.create_task(self._error_event.wait())
    try:
        done, _pending = await asyncio.wait(
            {event_task, error_task},
            return_when=asyncio.FIRST_COMPLETED,
            timeout=timeout,
        )
    finally:
        event_task.cancel()
        error_task.cancel()
    if not done:
        raise TimeoutError(f"Response not received in {timeout} seconds")
    if error_task in done:
        raise _ads_exc.DisconnectedError("Disconnected")
    return self.response


_BlockingRequest.wait = _patched_wait


ROUTER_HOST = "127.0.0.1"
ROUTER_PORT = 48898

OUR_NET_ID = "192.168.71.5.1.1"   # muss dem Router-NetId entsprechen
PLC_NET_ID = "169.254.34.222.1.1"


async def probe_layer1():
    print(f"[L1] Connect TCP zu Router {ROUTER_HOST}:{ROUTER_PORT} ...")
    reader, writer = await asyncio.wait_for(
        asyncio.open_connection(ROUTER_HOST, ROUTER_PORT), timeout=3.0
    )
    print(f"[L1] OK — Socket connected, peer={writer.get_extra_info('peername')}")
    writer.close()
    await writer.wait_closed()


async def probe_layer2():
    print(f"[L2] ads-async Client zu Router, our_net_id={OUR_NET_ID} ...")
    client = Client(
        their_address=(ROUTER_HOST, ROUTER_PORT),
        our_net_id=OUR_NET_ID,
        reconnect_rate=None,
        request_timeout=3.0,
    )
    try:
        await asyncio.wait_for(client.wait_for_connection(), timeout=4.0)
        print(f"[L2] OK — AMS-Channel zu Router etabliert")

        print(f"[L2] Öffne Circuit zur PLC NetId={PLC_NET_ID} ...")
        circuit = client.get_circuit(PLC_NET_ID)
        print(f"[L2] OK — Circuit-Objekt: {circuit}")

        print(f"[L3] Try get_device_information from PLC ...")
        async with asyncio.timeout(5.0):
            info = await circuit.get_device_information()
        print(f"[L3] OK — PLC sagt: {info}")
    finally:
        await client.close()


async def main():
    try:
        await probe_layer1()
    except Exception as e:
        print(f"[L1] FAIL: {type(e).__name__}: {e}")
        return 1
    try:
        await probe_layer2()
    except Exception as e:
        print(f"[L2/L3] FAIL: {type(e).__name__}: {e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
