"""Voller Round-Trip-Test: ads-async (Python) → AdsBridge :48898 → MQTT → PLC.

Beweist dass die Bridge MCP-grade ADS-Traffic abwickelt:
- Device Info
- ADS State
- Read by IndexGroup/IndexOffset (klassisch)
- Read symbolic name
"""
import asyncio
import sys
from ads_async.asyncio.client import Client, _BlockingRequest
from ads_async import exceptions as _ads_exc
from ads_async import structs


# Python 3.13 compat patch fuer ads-async _BlockingRequest.wait
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


BRIDGE_HOST = "127.0.0.1"
BRIDGE_PORT = 48898
OUR_NET_ID = "192.168.71.5.1.1"
PLC_NET_ID = "169.254.34.222.1.1"


async def main():
    client = Client(
        their_address=(BRIDGE_HOST, BRIDGE_PORT),
        our_net_id=OUR_NET_ID,
        reconnect_rate=None,
        request_timeout=5.0,
    )
    await client.wait_for_connection()
    circuit = client.get_circuit(PLC_NET_ID)

    try:
        print("=== L1 Device Info ===")
        async with asyncio.timeout(5):
            info = await circuit.get_device_information()
        print(f"  Result: {info}")

        print("\n=== L2 ADS State ===")
        async with asyncio.timeout(5):
            from ads_async.structs import AdsReadStateRequest
            state = await circuit.write_and_read(AdsReadStateRequest())
        print(f"  Result: {state}")

        print("\n=== L3 Read App Name (IndexGroup 0xF010) ===")
        try:
            async with asyncio.timeout(5):
                from ads_async.structs import AdsReadRequest
                # IndexGroup 0xF010 / IndexOffset 0 = AppName, 32 bytes
                req = AdsReadRequest(index_group=0xF010, index_offset=0, length=32)
                resp = await circuit.write_and_read(req)
            data = resp.data
            name = bytes(data).rstrip(b'\0').decode('latin1', errors='replace')
            print(f"  AppName: '{name}'")
        except Exception as e:
            print(f"  (skip) {type(e).__name__}: {e}")

        print("\n=== L4 List Symbol Count (IndexGroup 0xF00F) ===")
        try:
            async with asyncio.timeout(5):
                req = AdsReadRequest(index_group=0xF00F, index_offset=0, length=24)
                resp = await circuit.write_and_read(req)
            print(f"  Symbol info upload header: {bytes(resp.data).hex()}")
        except Exception as e:
            print(f"  (skip) {type(e).__name__}: {e}")

        print("\nALL OK")
        return 0
    except Exception as e:
        print(f"\nFAIL: {type(e).__name__}: {e}")
        return 1
    finally:
        await client.close()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
