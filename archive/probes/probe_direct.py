"""Probe: ads-async direkt an PLC TCP/AMS — kein Router dazwischen."""
import asyncio
import sys
from ads_async.asyncio.client import Client, _BlockingRequest
from ads_async import exceptions as _ads_exc


async def _patched_wait(self, timeout=2.0):
    event_task = asyncio.create_task(self._event.wait())
    error_task = asyncio.create_task(self._error_event.wait())
    try:
        done, _ = await asyncio.wait(
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


PLC_IP = "192.168.71.38"
PLC_PORT_TCP = 48898
PLC_NET_ID = "169.254.34.222.1.1"
OUR_NET_ID = "192.168.71.5.1.1"


async def main():
    print(f"[L1] TCP direct connect zu PLC {PLC_IP}:{PLC_PORT_TCP} ...")
    reader, writer = await asyncio.wait_for(
        asyncio.open_connection(PLC_IP, PLC_PORT_TCP), timeout=4.0
    )
    print(f"[L1] OK — Socket: {writer.get_extra_info('peername')}")
    writer.close()
    await writer.wait_closed()

    print(f"[L2] ads-async Client direkt an PLC, our_net_id={OUR_NET_ID} ...")
    client = Client(
        their_address=(PLC_IP, PLC_PORT_TCP),
        our_net_id=OUR_NET_ID,
        reconnect_rate=None,
        request_timeout=3.0,
    )
    try:
        await asyncio.wait_for(client.wait_for_connection(), timeout=5.0)
        print(f"[L2] OK — AMS-Channel direkt zur PLC etabliert")

        circuit = client.get_circuit(PLC_NET_ID)
        print(f"[L3] get_device_information ...")
        async with asyncio.timeout(5.0):
            info = await circuit.get_device_information()
        print(f"[L3] OK — Device info: {info}")
    except Exception as e:
        print(f"[L2/L3] FAIL: {type(e).__name__}: {e}")
        return 1
    finally:
        await client.close()
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
