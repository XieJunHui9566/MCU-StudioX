"""StudioX API 2 示例：纯标准库 JSON 行协议，不执行 shell 命令。

发行时由 plugin.json 指定插件目录中的 python.exe 和相对 plugin.py。
线程中的命令可等待 hostCall，同时主线程持续处理 response/cancel。
"""

import json
import sys
import threading
import time
import traceback


MAX_LINE = 1024 * 1024
write_lock = threading.Lock()
state_lock = threading.Lock()
pending_lock = threading.Lock()
pending = {}
requests = {}
next_id = 0
counter = 0
active = False


class CallCancelled(Exception):
    """调用者取消只结束当前命令，不终止整个插件会话。"""


def send(kind, request_id=None, method=None, payload=None, error_code=None, error=None):
    message = {"protocolVersion": 2, "kind": kind}
    for key, value in (
        ("requestId", request_id),
        ("method", method),
        ("payload", payload),
        ("errorCode", error_code),
        ("error", error),
    ):
        if value is not None:
            message[key] = value
    line = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
    if len(line) > MAX_LINE:
        raise ValueError("协议消息过长")
    with write_lock:
        sys.stdout.write(line + "\n")
        sys.stdout.flush()


def panel():
    return {
        "id": "python.status",
        "title": "Python 插件",
        "widgets": [
            {"id": "counter", "kind": "text", "label": "计数", "value": str(counter)},
            {
                "id": "increment",
                "kind": "button",
                "label": "增加计数",
                "commandId": "increment",
            },
        ],
    }


def describe():
    return {
        "commands": [
            {"id": "increment", "title": "Python：增加计数", "placement": "palette"},
            {"id": "workspace", "title": "Python：读取工作区", "placement": "palette"},
            {"id": "delay", "title": "Python：可取消等待", "placement": "palette"},
        ],
        "panels": [panel()],
        "agentTools": [
            {
                "id": "workspace",
                "description": "通过 StudioX 主机工具读取当前工作区。",
                "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
            }
        ],
    }


def call_host(tool, arguments, cancelled):
    global next_id
    with pending_lock:
        next_id += 1
        request_id = "python-host-" + str(next_id)
        completion = {"event": threading.Event(), "response": None}
        pending[request_id] = completion
    try:
        send("request", request_id, "hostCall", {"tool": tool, "arguments": arguments})
        while not completion["event"].wait(0.05):
            if cancelled.is_set():
                send("cancel", request_id)
                raise CallCancelled("主机工具已取消")
        response = completion["response"]
        if response.get("errorCode"):
            raise RuntimeError(response.get("error", response["errorCode"]))
        return response["payload"]
    finally:
        with pending_lock:
            pending.pop(request_id, None)


def handle_request(message, cancelled):
    global active, counter
    request_id = message["requestId"]
    try:
        method = message["method"]
        payload = message.get("payload", {})
        if method == "describe":
            result = describe()
        elif method == "activate":
            active = True
            send("event", method="log", payload={"level": "info", "message": "Python 插件已激活"})
            send("event", method="panel", payload=panel())
            result = {"active": True}
        elif method == "deactivate":
            active = False
            result = {"active": False}
        elif method == "invoke":
            if not active:
                raise RuntimeError("插件尚未激活")
            command = payload["id"]
            arguments = payload.get("arguments", {})
            if command == "increment":
                with state_lock:
                    counter += 1
                    result = {"count": counter}
                    send("event", method="panel", payload=panel())
            elif command == "workspace":
                result = call_host("project_info", arguments, cancelled)
            elif command == "delay":
                if cancelled.wait(30):
                    raise CallCancelled("调用者取消等待")
                result = {"completed": True}
            else:
                raise ValueError("未声明的命令：" + command)
        else:
            raise ValueError("未知协议方法：" + method)
        if cancelled.is_set():
            raise CallCancelled("调用者已取消")
        send("response", request_id, payload=result)
    except Exception as error:
        send(
            "response",
            request_id,
            error_code="PLUGIN_CANCELLED" if isinstance(error, CallCancelled) else "PLUGIN_EXCEPTION",
            error=traceback.format_exc(),
        )
    finally:
        with pending_lock:
            requests.pop(request_id, None)


def main():
    # 明确 UTF-8；Python 启动时的用户配置和环境变量不参与协议编码。
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    while True:
        line = sys.stdin.readline(MAX_LINE + 2)
        if not line:
            break
        if len(line) > MAX_LINE or not line.endswith("\n"):
            raise ValueError("协议行过长或未结束")
        message = json.loads(line)
        if message.get("protocolVersion") != 2:
            raise ValueError("只支持协议版本 2")
        kind = message.get("kind")
        request_id = message.get("requestId")
        if kind == "response":
            with pending_lock:
                completion = pending.get(request_id)
                if completion:
                    completion["response"] = message
                    completion["event"].set()
        elif kind == "cancel":
            with pending_lock:
                cancelled = requests.get(request_id)
                if cancelled:
                    cancelled.set()
        elif kind == "request" and request_id and message.get("method"):
            with pending_lock:
                if request_id in requests or len(requests) >= 32:
                    raise ValueError("重复请求或超过并发限制")
                cancelled = threading.Event()
                requests[request_id] = cancelled
            threading.Thread(target=handle_request, args=(message, cancelled), daemon=True).start()
        else:
            raise ValueError("无效协议消息")
    with pending_lock:
        for cancelled in requests.values():
            cancelled.set()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
