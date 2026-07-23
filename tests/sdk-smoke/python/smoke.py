"""Python SDK smoke test against OpenServiceBus (pyamqp stack).

Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
  send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.

Exit code 0 = all pass; 1 = at least one failure.
"""

import os
import sys
import time
from datetime import datetime, timedelta, timezone

from azure.servicebus import ServiceBusClient, ServiceBusMessage

CONN = os.environ.get(
    "SMOKE_CONNECTION",
    "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;"
    "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true",
)

results: list[str] = []


def check(name: str, ok: bool, extra: str = "") -> None:
    results.append(f"{'PASS' if ok else 'FAIL'} {name}{' ' + extra if extra else ''}")


def run() -> None:
    stamp = format(int(time.time() * 1000), "x")
    message_id = f"python-{stamp}"

    with ServiceBusClient.from_connection_string(CONN, retry_total=1) as client:
        # 1. send - exercises $cbs put-token.
        with client.get_queue_sender("smoke-queue") as sender:
            sender.send_messages(ServiceBusMessage("hello from python", message_id=message_id))
            check("send", True)

            with client.get_queue_receiver("smoke-queue", max_wait_time=10) as receiver:
                # 2. peek - $management request/response.
                peeked = receiver.peek_messages(max_message_count=10)
                check("peek", any(m.message_id == message_id for m in peeked))

                # 3. receive + 4. complete.
                msgs = receiver.receive_messages(max_message_count=1, max_wait_time=10)
                got = len(msgs) == 1 and str(msgs[0]) == "hello from python"
                check("receive", got)
                if msgs:
                    receiver.complete_message(msgs[0])
                check("complete", got)

            # 5. schedule + 6. cancelSchedule - two more $management operations.
            due = datetime.now(timezone.utc) + timedelta(minutes=5)
            seqs = sender.schedule_messages(ServiceBusMessage("future"), due)
            check("schedule", len(seqs) == 1)
            sender.cancel_scheduled_messages(seqs)
            check("cancelSchedule", True)

        # 7. session receive - send into a session, accept it, receive in it.
        session_id = f"python-session-{stamp}"
        with client.get_queue_sender("smoke-sessions") as session_sender:
            session_sender.send_messages(ServiceBusMessage("session msg", session_id=session_id))

        with client.get_queue_receiver("smoke-sessions", session_id=session_id, max_wait_time=10) as session:
            smsgs = session.receive_messages(max_message_count=1, max_wait_time=10)
            got = len(smsgs) == 1 and str(smsgs[0]) == "session msg"
            check("session receive", got)
            if smsgs:
                session.complete_message(smsgs[0])


try:
    run()
except Exception as e:  # noqa: BLE001 - a smoke test reports, it doesn't crash
    check("EXCEPTION", False, str(e)[:300])

print("\n".join(results))
failed = any(r.startswith("FAIL") for r in results)
print("PYTHON SMOKE: FAILED" if failed else "PYTHON SMOKE: ALL PASS")
sys.exit(1 if failed else 0)
