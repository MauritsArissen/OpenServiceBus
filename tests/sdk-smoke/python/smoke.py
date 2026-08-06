"""Python SDK smoke test against OpenServiceBus (pyamqp stack).

Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
  send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
  -> topic session receive -> admin create/get/roundtrip/size limit/status gate/delete detach/delete (ATOM management API)
  -> purge (JSON management API) -> transfer dlq -> sql filter
against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.

Exit code 0 = all pass; 1 = at least one failure.
"""

import os
import re
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

from azure.servicebus import ServiceBusClient, ServiceBusMessage, ServiceBusSubQueue

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
            "<MaxDeliveryCount>7</MaxDeliveryCount><AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle><MaxSizeInMegabytes>2048</MaxSizeInMegabytes></QueueDescription></content></entry>"
        )

        status, _ = http("PUT", f"{base}/{admin_queue}?api-version=2021-05", queue_xml.encode())
        check("admin create queue", status == 201, "" if status == 201 else f"status {status}")

        status, body = http("GET", f"{base}/{admin_queue}?api-version=2021-05")
        check("admin get queue", status == 200 and "<MaxDeliveryCount>7</MaxDeliveryCount>" in body and "<AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle>" in body and "<MaxSizeInMegabytes>2048</MaxSizeInMegabytes>" in body)

        # The admin-created queue must be immediately usable on the data plane.
        with client.get_queue_sender(admin_queue) as admin_sender:
            admin_sender.send_messages(ServiceBusMessage("admin roundtrip"))
        with client.get_queue_receiver(admin_queue, max_wait_time=10) as admin_receiver:
            amsgs = admin_receiver.receive_messages(max_message_count=1, max_wait_time=10)
            got = len(amsgs) == 1 and str(amsgs[0]) == "admin roundtrip"
            check("admin roundtrip", got)
            if amsgs:
                admin_receiver.complete_message(amsgs[0])

        # 13. admin size limit - a 300 KB message must be rejected against the default
        # 256 KB limit the sender link advertises (issue #24).
        oversize_blocked = False
        try:
            with client.get_queue_sender(admin_queue) as oversize_sender:
                oversize_sender.send_messages(ServiceBusMessage(b"x" * (300 * 1024)))
        except Exception:  # noqa: BLE001 - any SDK error counts as "send rejected"
            oversize_blocked = True
        check("admin size limit", oversize_blocked)

        # 14. admin status gate - SendDisabled rejects sends, Active restores them (issue #22).
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

        # 15. admin delete detach - deleting the queue while a receive is blocked on it
        # must detach the link promptly instead of stalling until the SDK's timeout
        # (issue #36). The delete fires from a timer thread while the main thread waits
        # in receive_messages.
        doomed_receiver = client.get_queue_receiver(admin_queue)
        leftover = doomed_receiver.receive_messages(max_message_count=1, max_wait_time=10)
        if leftover:
            doomed_receiver.complete_message(leftover[0])

        delete_status: "list[int]" = []
        deleter = threading.Timer(
            0.5, lambda: delete_status.append(http("DELETE", f"{base}/{admin_queue}?api-version=2021-05")[0])
        )
        deleter.start()
        started = time.monotonic()
        try:
            doomed_receiver.receive_messages(max_message_count=1, max_wait_time=30)
        except Exception:  # noqa: BLE001 - a not-found detach error is the expected shape
            pass
        elapsed = time.monotonic() - started
        deleter.join()
        try:
            doomed_receiver.close()
        except Exception:  # noqa: BLE001 - closing a detached receiver may re-raise
            pass
        check("admin delete detach", elapsed < 10, f"{elapsed:.1f}s")

        gone, _ = http("GET", f"{base}/{admin_queue}?api-version=2021-05")
        check("admin delete queue", delete_status == [200] and gone == 404)

        # 16. purge - the emulator-native message purge on the JSON management API
        # (issue #36): every message goes, the queue itself stays.
        mgmt_base = os.environ.get("SMOKE_MANAGEMENT", "http://localhost:5300")
        purge_queue = f"smoke-purge-python-{stamp}"
        empty_queue_xml = (
            '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">'
            '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>'
            "</content></entry>"
        )
        http("PUT", f"{base}/{purge_queue}?api-version=2021-05", empty_queue_xml.encode())
        with client.get_queue_sender(purge_queue) as purge_sender:
            purge_sender.send_messages(ServiceBusMessage("one"))
            purge_sender.send_messages(ServiceBusMessage("two"))
        purge_status, purge_body = http("DELETE", f"{mgmt_base}/queues/{purge_queue}/messages")
        with client.get_queue_receiver(purge_queue, max_wait_time=2) as purge_receiver:
            after_purge = purge_receiver.receive_messages(max_message_count=1, max_wait_time=2)
        check(
            "admin purge",
            purge_status == 200 and '"purged":2' in purge_body.replace(" ", "") and not after_purge,
            "" if purge_status == 200 else f"status {purge_status}",
        )
        http("DELETE", f"{base}/{purge_queue}?api-version=2021-05")

        # 17. transfer dlq - a send whose auto-forward target was deleted lands in the
        # source queue's $Transfer/$DeadLetterQueue with a descriptive reason (issue #25).
        fwd_target = f"smoke-fwd-target-{stamp}"
        fwd_source = f"smoke-fwd-{stamp}"
        http("PUT", f"{base}/{fwd_target}?api-version=2021-05", empty_queue_xml.encode())
        fwd_xml = (
            '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">'
            '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">'
            f"<ForwardTo>{fwd_target}</ForwardTo></QueueDescription></content></entry>"
        )
        http("PUT", f"{base}/{fwd_source}?api-version=2021-05", fwd_xml.encode())
        http("DELETE", f"{base}/{fwd_target}?api-version=2021-05")
        with client.get_queue_sender(fwd_source) as fwd_sender:
            fwd_sender.send_messages(ServiceBusMessage("undeliverable"))
        with client.get_queue_receiver(
            fwd_source, sub_queue=ServiceBusSubQueue.TRANSFER_DEAD_LETTER, max_wait_time=10
        ) as transfer_receiver:
            moved = transfer_receiver.receive_messages(max_message_count=1, max_wait_time=10)
            got = len(moved) == 1 and moved[0].dead_letter_reason == "MessagingEntityNotFound"
            check(
                "transfer dlq",
                got,
                "" if got else ("no message" if not moved else (moved[0].dead_letter_reason or "no reason")),
            )
            if moved:
                transfer_receiver.complete_message(moved[0])
        http("DELETE", f"{base}/{fwd_source}?api-version=2021-05")

        # 18. sql filter - an arithmetic SQL rule routes only matching messages (issue #26).
        filter_topic = f"smoke-filter-{stamp}"

        def atom_entry(description_xml: str) -> bytes:
            return (
                '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">'
                + description_xml
                + "</content></entry>"
            ).encode()

        http("PUT", f"{base}/{filter_topic}?api-version=2021-05", atom_entry(
            '<TopicDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>'))
        http("PUT", f"{base}/{filter_topic}/subscriptions/flt?api-version=2021-05", atom_entry(
            '<SubscriptionDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>'))
        http("DELETE", f"{base}/{filter_topic}/subscriptions/flt/rules/$Default?api-version=2021-05")
        rule_status, _ = http("PUT", f"{base}/{filter_topic}/subscriptions/flt/rules/high?api-version=2021-05", atom_entry(
            '<RuleDescription xmlns:i="http://www.w3.org/2001/XMLSchema-instance" '
            'xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">'
            '<Filter i:type="SqlFilter"><SqlExpression>priority + 1 >= 5</SqlExpression></Filter></RuleDescription>'))
        with client.get_topic_sender(filter_topic) as filter_sender:
            filter_sender.send_messages(ServiceBusMessage(
                "match", message_id=f"flt-match-{stamp}", application_properties={"priority": 4}))
            filter_sender.send_messages(ServiceBusMessage(
                "miss", message_id=f"flt-miss-{stamp}", application_properties={"priority": 1}))
        with client.get_subscription_receiver(filter_topic, "flt", max_wait_time=10) as filter_receiver:
            filtered = filter_receiver.receive_messages(max_message_count=1, max_wait_time=10)
            if filtered:
                filter_receiver.complete_message(filtered[0])
            extra = filter_receiver.receive_messages(max_message_count=1, max_wait_time=2)
        filter_ok = (
            rule_status == 201
            and len(filtered) == 1
            and filtered[0].message_id == f"flt-match-{stamp}"
            and not extra
        )
        check(
            "sql filter",
            filter_ok,
            "" if filter_ok else f"rule {rule_status}, got {len(filtered)} msg(s), extra {len(extra)}",
        )
        http("DELETE", f"{base}/{filter_topic}?api-version=2021-05")


try:
    run()
except Exception as e:  # noqa: BLE001 - a smoke test reports, it doesn't crash
    check("EXCEPTION", False, str(e)[:300])

print("\n".join(results))
failed = any(r.startswith("FAIL") for r in results)
print("PYTHON SMOKE: FAILED" if failed else "PYTHON SMOKE: ALL PASS")
sys.exit(1 if failed else 0)
