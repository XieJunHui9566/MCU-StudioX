//! Rust 原生 EXE 插件示例；stdout 只承载协议，日志写入 stderr。
use serde_json::{Value, json};
use std::collections::HashSet;
use std::io::{self, BufRead, Read, Write};

const MAX_LINE_BYTES: usize = 1024 * 1024;

struct Plugin<R: BufRead, W: Write> {
    input: R,
    output: W,
    next_id: u64,
    cancelled: HashSet<String>,
    active: HashSet<String>,
}

impl<R: BufRead, W: Write> Plugin<R, W> {
    fn send(&mut self, message: Value) -> Result<(), String> {
        let text = serde_json::to_string(&message).map_err(|error| error.to_string())?;
        if text.len() > MAX_LINE_BYTES {
            return Err("protocol output exceeds limit".into());
        }
        writeln!(self.output, "{text}").map_err(|error| error.to_string())?;
        self.output.flush().map_err(|error| error.to_string())
    }

    fn receive(&mut self) -> Result<Option<Value>, String> {
        // take 的上限阻止没有换行的对端无限增长本进程内存。
        let mut bytes = Vec::new();
        let mut limited = (&mut self.input).take((MAX_LINE_BYTES + 1) as u64);
        let count = limited.read_until(b'\n', &mut bytes).map_err(|error| error.to_string())?;
        if count == 0 {
            return Ok(None);
        }
        if count > MAX_LINE_BYTES {
            return Err("protocol input exceeds limit".into());
        }
        let message: Value = serde_json::from_slice(&bytes).map_err(|error| error.to_string())?;
        if message["protocolVersion"] != 2 {
            return Err("unsupported protocol version".into());
        }
        Ok(Some(message))
    }

    fn event(&mut self, method: &str, payload: Value) -> Result<(), String> {
        self.send(json!({"protocolVersion":2,"kind":"event","method":method,"payload":payload}))
    }

    fn host_call(&mut self, current: &str, tool: &str) -> Result<Value, String> {
        self.next_id += 1;
        let id = format!("rust-host-{}", self.next_id);
        self.send(json!({"protocolVersion":2,"kind":"request","requestId":id,"method":"hostCall",
            "payload":{"tool":tool,"arguments":{}}}))?;
        loop {
            let message = self.receive()?.ok_or("host closed protocol")?;
            if message["kind"] == "response" && message["requestId"] == id {
                if self.cancelled.remove(current) {
                    return Err("PLUGIN_CANCELLED".into());
                }
                if !message["errorCode"].is_null() {
                    return Err(message["error"].as_str().unwrap_or("host tool failed").to_owned());
                }
                return Ok(message["payload"].clone());
            }
            // 双向消息必须继续读取；不能把 hostCall 的等待循环当成单向响应读取器。
            self.dispatch(message)?;
        }
    }

    fn dispatch(&mut self, message: Value) -> Result<(), String> {
        let id = message["requestId"].as_str().unwrap_or("").to_owned();
        if message["kind"] == "cancel" {
            if self.active.contains(&id) {
                self.cancelled.insert(id);
            }
            return Ok(());
        }
        if message["kind"] == "response" {
            // 已取消请求的迟到结果不能被交给另一项请求。
            return Ok(());
        }
        if message["kind"] != "request" || id.is_empty() {
            return Err("unexpected protocol message".into());
        }
        if self.active.len() >= 32 || !self.active.insert(id.clone()) {
            return Err("duplicate or too many requests".into());
        }
        let method = message["method"].as_str().unwrap_or("");
        let result = match method {
            "describe" => Ok(json!({
                "commands":[{"id":"refresh","title":"Refresh Rust overview","placement":"tools"}],
                "panels":[panel(json!({"status":"Waiting for host snapshot"}))],
                "agentTools":[{"id":"overview","description":"Read current project through the approved host broker.",
                    "inputSchema":{"type":"object","properties":{},"additionalProperties":false}}]
            })),
            "activate" => {
                self.event("log", json!({"level":"info","message":"Rust native plugin activated"}))?;
                Ok(json!({}))
            }
            "deactivate" => Ok(json!({})),
            "invoke" => {
                let payload = &message["payload"];
                if (payload["kind"] == "command" && payload["id"] == "refresh")
                    || (payload["kind"] == "agentTool" && payload["id"] == "overview") {
                    self.host_call(&id, "project_info").and_then(|project| {
                        self.event("panel", panel(project.clone()))?;
                        Ok(project)
                    })
                } else {
                    Err("unregistered contribution".into())
                }
            }
            _ => Err("unsupported method".into()),
        };
        let response = match result {
            Ok(payload) => json!({"protocolVersion":2,"kind":"response","requestId":id,"payload":payload}),
            Err(error) => json!({"protocolVersion":2,"kind":"response","requestId":id,
                "errorCode":if error == "PLUGIN_CANCELLED" {"PLUGIN_CANCELLED"} else {"RUST_PLUGIN"},"error":error}),
        };
        self.active.remove(&id);
        self.cancelled.remove(&id);
        self.send(response)
    }

    fn run(&mut self) -> Result<(), String> {
        while let Some(message) = self.receive()? {
            self.dispatch(message)?;
        }
        Ok(())
    }
}

fn panel(project: Value) -> Value {
    json!({"id":"overview","title":"Rust project overview","widgets":[
        {"id":"project","kind":"text","label":"project_info","value":project.to_string()},
        {"id":"refresh","kind":"button","label":"Refresh","commandId":"refresh"}
    ]})
}

fn main() {
    let mut plugin = Plugin { input: io::stdin().lock(), output: io::stdout().lock(),
        next_id: 0, cancelled: HashSet::new(), active: HashSet::new() };
    if let Err(error) = plugin.run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
