"""Python SDK smoke test against OpenServiceBus (pyamqp stack).

Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
  send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
  -> topic session receive -> admin create/get/roundtrip/status gate/delete (ATOM management API)
against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.

Exit code 0 = all pass; 1 = at least one failure.
"""

import os
import re
import sys
import time
import urllib.error
import urllib.request
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


def http(method: str, url: str, body: "bytes | None" = None) -> "tuple[int, str]":
    """Bare HTTP helper for the ATOM management steps; returns (status, body)."""
    request = urllib.request.Request(url, data=body, method=method)
    if body is not None:
        request.add_header("Content-Type", "application/atom+xml")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


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

        # 8. topic session receive - publish with a session id, accept the session on a
        # session-enabled SUBSCRIPTION (regression guard for issue #18).
        topic_session_id = f"python-topic-session-{stamp}"
        with client.get_topic_sender("smoke-topic") as topic_sender:
            topic_sender.send_messages(ServiceBusMessage("topic session msg", session_id=topic_session_id))

        with client.get_subscription_receiver(
            "smoke-topic", "smoke-topic-sessions", session_id=topic_session_id, max_wait_time=10
        ) as topic_session:
            tmsgs = topic_session.receive_messages(max_message_count=1, max_wait_time=10)
            got = len(tmsgs) == 1 and str(tmsgs[0]) == "topic session msg"
            check("topic session receive", got)
            if tmsgs:
                topic_session.complete_message(tmsgs[0])

        # 9-12. admin entity CRUD over the ATOM management API served on the same port
        # as AMQP. The Python SDK's ServiceBusAdministrationClient cannot target a
        # plaintext emulator yet - azure-servicebus 7.14.3 hardcodes the scheme
        # (_management_client.py: `self._endpoint = "https://" + fully_qualified_namespace`)
        # - so this exercises the exact same protocol with plain HTTP until the SDK
        # catches up.
        base = "http://" + re.search(r"Endpoint=sb://([^;/]+)", CONN, re.I).group(1)
        admin_queue = f"smoke-admin-python-{stamp}"
        queue_xml = (
            '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">'
            '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">'
            "<MaxDeliveryCount>7</MaxDeliveryCount></QueueDescription></content></entry>"
        )

        status, _ = http("PUT", f"{base}/{admin_queue}?api-version=2021-05", queue_xml.encode())
        check("admin create queue", status == 201, "" if status == 201 else f"status {status}")

        status, body = http("GET", f"{base}/{admin_queue}?api-version=2021-05")
        check("admin get queue", status == 200 and "<MaxDeliveryCount>7</MaxDeliveryCount>" in body)

        # The admin-created queue must be immediately usable on the data plane.
        with client.get_queue_sender(admin_queue) as admin_sender:
            admin_sender.send_messages(ServiceBusMessage("admin roundtrip"))
        with client.get_queue_receiver(admin_queue, max_wait_time=10) as admin_receiver:
            amsgs = admin_receiver.receive_messages(max_message_count=1, max_wait_time=10)
            got = len(amsgs) == 1 and str(amsgs[0]) == "admin roundtrip"
            check("admin roundtrip", got)
            if amsgs:
                admin_receiver.complete_message(amsgs[0])

        # 13. admin status gate - SendDisabled rejects sends, Active restores them (issue #22).
        def status_xml(entity_status: str) -> bytes:
            return (
                '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">'
                '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">'
                f"<MaxDeliveryCount>7</MaxDeliveryCount><Status>{entity_status}</Status></QueueDescription></content></entry>"
            ).encode()

        def put_status(entity_status: str) -> int:
            request = urllib.request.Request(
                f"{base}/{admin_queue}?api-version=2021-05", data=status_xml(entity_status), method="PUT"
            )
            request.add_header("Content-Type", "application/atom+xml")
            request.add_header("If-Match", "*")
            try:
                with urllib.request.urlopen(request, timeout=10) as response:
                    return response.status
            except urllib.error.HTTPError as e:
                return e.code

        put_status("SendDisabled")
        blocked = False
        try:
            with client.get_queue_sender(admin_queue) as blocked_sender:
                blocked_sender.send_messages(ServiceBusMessage("must not land"))
        except Exception:  # noqa: BLE001 - any SDK error counts as "send rejected"
            blocked = True
        put_status("Active")
        with client.get_queue_sender(admin_queue) as reopened_sender:
            reopened_sender.send_messages(ServiceBusMessage("flowing again"))
        check("admin status gate", blocked)

        status, _ = http("DELETE", f"{base}/{admin_queue}?api-version=2021-05")
        gone, _ = http("GET", f"{base}/{admin_queue}?api-version=2021-05")
        check("admin delete queue", status == 200 and gone == 404)


try:
    run()
except Exception as e:  # noqa: BLE001 - a smoke test reports, it doesn't crash
    check("EXCEPTION", False, str(e)[:300])

print("\n".join(results))
failed = any(r.startswith("FAIL") for r in results)
print("PYTHON SMOKE: FAILED" if failed else "PYTHON SMOKE: ALL PASS")
sys.exit(1 if failed else 0)
