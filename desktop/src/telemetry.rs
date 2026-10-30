use chrono::{DateTime, Utc};
use reqwest::blocking::Client;
use serde_json::{json, Value};
use std::{
    collections::VecDeque,
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Sender},
        Arc, RwLock,
    },
    thread,
    time::{Duration, Instant},
};

const PORT: u16 = 3060;
const MAX_BYTES: u64 = 32 * 1024 * 1024;

#[derive(Clone, Debug)]
pub struct Metric {
    pub field: String,
    pub label: String,
    pub value: Option<f64>,
    pub raw: String,
    pub unit: String,
    pub source: String,
    pub state: String,
    pub timestamp_ms: Option<i64>,
    pub index: u64,
}

#[derive(Clone, Default)]
pub struct Snapshot {
    pub gpu: Value,
    pub public: Value,
    pub research: Value,
    pub windows: Value,
    pub capabilities: Value,
    pub full: Option<Arc<Value>>,
    pub history: Option<Arc<Value>>,
    pub status: String,
    pub error: Option<String>,
    pub connected: bool,
    pub backend_owned: bool,
    pub last_update_ms: i64,
    pub export_path: Option<PathBuf>,
    pub export_error: Option<String>,
    pub samples: VecDeque<(i64, Option<f64>, Option<f64>)>,
}

impl Snapshot {
    pub fn metric(&self, field: &str) -> Option<f64> {
        self.metrics()
            .into_iter()
            .find(|m| m.field == field && m.value.is_some())
            .and_then(|m| m.value)
    }
    pub fn metrics(&self) -> Vec<Metric> {
        let mut result = Vec::new();
        for row in self.public["fields"].as_array().into_iter().flatten() {
            let field = string(&row["field"]);
            let native_value = [&row["value_u64"], &row["value_i64"], &row["value_f64"]]
                .into_iter()
                .find(|v| !v.is_null());
            let timestamp = row["timestamp_unix_ms"].as_i64();
            let fresh = self.connected && valid_age(timestamp, 12_000);
            let available = row["state"] == "available" && fresh;
            let raw = native_value
                .map(|v| v.to_string())
                .unwrap_or_else(|| "—".into());
            let value = native_value.and_then(|v| safe_f64(v)).filter(|_| available);
            result.push(Metric {
                label: label(&field).into(),
                field,
                value,
                raw: if available { raw } else { "—".into() },
                unit: string(&row["unit"]),
                source: format!(
                    "{} · {}",
                    string(&row["provider"]),
                    row["provider_native_id"]
                ),
                state: if row["state"] == "available" && !fresh {
                    "stale".into()
                } else {
                    string(&row["state"])
                },
                timestamp_ms: timestamp,
                index: row["provider_native_id"].as_u64().unwrap_or(0),
            });
        }
        for row in self.research["collection"]["readings"]
            .as_array()
            .into_iter()
            .flatten()
        {
            let field = string(&row["field"]);
            let timestamp = row["captured_at"].as_str().and_then(parse_ms);
            let limit = row["max_age_ms"]
                .as_i64()
                .unwrap_or(30_000)
                .clamp(1, 180_000);
            let fresh = self.connected && valid_age(timestamp, limit);
            let available = row["state"] == "available" && fresh;
            result.push(Metric {
                label: label(&field).into(),
                field,
                value: safe_f64(&row["value"]).filter(|_| available),
                raw: if available {
                    row["raw_value"].to_string()
                } else {
                    "—".into()
                },
                unit: string(&row["unit"]),
                source: format!("NVAPI {}", string(&row["interface_id"])),
                state: if !fresh {
                    "stale".into()
                } else {
                    string(&row["state"])
                },
                timestamp_ms: timestamp,
                index: row["channel_index"].as_u64().unwrap_or(0),
            });
        }
        result.extend(self.windows_metrics());
        result
    }
    pub fn windows_metrics(&self) -> Vec<Metric> {
        let timestamp = self.windows["captured_at_unix_ms"].as_i64();
        let fresh = self.connected
            && valid_age(timestamp, 12_000)
            && matches!(
                self.windows["state"].as_str(),
                Some("available" | "partial")
            )
            && self.windows["gpu"]["uuid"]
                .as_str()
                .is_some_and(|uuid| Some(uuid) == self.gpu["uuid"].as_str());
        let mut result = Vec::new();
        let mut append = |field: String, label: String, value: &Value, index: u64| {
            let available = fresh && value["state"] == "available";
            result.push(Metric {
                field,
                label,
                value: safe_f64(&value["value"]).filter(|_| available),
                raw: if available {
                    value["value"].to_string()
                } else {
                    "—".into()
                },
                unit: string(&value["unit"]),
                source: "Windows DXGI / PDH".into(),
                state: if !fresh {
                    "stale".into()
                } else {
                    string(&value["state"])
                },
                timestamp_ms: timestamp,
                index,
            });
        };
        for (i, (name, label)) in [
            ("local_memory", "Memória local Windows"),
            ("non_local_memory", "Memória compartilhada Windows"),
        ]
        .into_iter()
        .enumerate()
        {
            if !self.windows[name].is_null() {
                append(
                    format!("windows_{name}_bytes"),
                    label.into(),
                    &self.windows[name],
                    i as u64,
                );
            }
        }
        for (i, engine) in self.windows["engines"]
            .as_array()
            .into_iter()
            .flatten()
            .enumerate()
        {
            let name = string(&engine["engine_type"]);
            append(
                format!("windows_engine_{name}_percent"),
                name,
                &engine["utilization"],
                i as u64,
            );
        }
        result
    }
    pub fn fans(&self) -> [Option<f32>; 2] {
        let metrics = self.metrics();
        [0, 1].map(|index| {
            metrics
                .iter()
                .find(|m| m.field == "fan_speed_intended_rpm" && m.index == index)
                .and_then(|m| m.value)
                .map(|v| v as f32)
        })
    }
}

pub fn now_ms() -> i64 {
    Utc::now().timestamp_millis()
}
pub fn string(value: &Value) -> String {
    value.as_str().unwrap_or_default().to_owned()
}
pub fn parse_ms(value: &str) -> Option<i64> {
    DateTime::parse_from_rfc3339(value)
        .ok()
        .map(|t| t.timestamp_millis())
}
pub fn valid_age(timestamp: Option<i64>, limit: i64) -> bool {
    timestamp
        .map(|t| {
            let age = now_ms() - t;
            (-5000..=limit).contains(&age)
        })
        .unwrap_or(false)
}
fn safe_f64(value: &Value) -> Option<f64> {
    if value.as_u64().is_some_and(|v| v > 9_007_199_254_740_991)
        || value.as_i64().is_some_and(|v| v < -9_007_199_254_740_991)
    {
        return None;
    }
    value.as_f64().filter(|v| v.is_finite())
}
pub fn label(field: &str) -> &str {
    match field {
        "gpu_die_temperature_c" => "Temperatura da GPU",
        "gpu_hotspot_temperature_c" => "Máximo / hotspot GPU",
        "gpu_core_voltage_v" => "Tensão do núcleo",
        "clock_graphics_mhz" => "Clock gráfico",
        "clock_sm_mhz" => "Clock SM",
        "clock_memory_mhz" => "Clock da memória",
        "clock_video_mhz" => "Clock de vídeo",
        "power_average_mw" => "Potência média",
        "power_instant_mw" => "Potência instantânea",
        "memory_used_bytes" => "VRAM utilizada",
        "memory_total_bytes" => "VRAM total",
        "fan_speed_intended_rpm" => "RPM informado pelo driver",
        "fan_speed_percent" => "Ventoinha (%)",
        "utilization_gpu_percent" => "Utilização da GPU",
        "memory_temperature_c" => "Temperatura da memória",
        "performance_state" => "Estado de desempenho",
        "total_energy_mj" => "Energia acumulada",
        _ => field,
    }
}

enum Action {
    Full,
    History,
    Export,
}
pub struct Telemetry {
    state: Arc<RwLock<Snapshot>>,
    sender: Sender<Action>,
    stop: Arc<AtomicBool>,
}
impl Telemetry {
    pub fn start() -> Self {
        let state = Arc::new(RwLock::new(Snapshot {
            status: "Preparando coleta local…".into(),
            ..Default::default()
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let (sender, receiver) = mpsc::channel();
        let shared = state.clone();
        let stopping = stop.clone();
        thread::Builder::new()
            .name("rtx-readonly-collector".into())
            .spawn(move || {
                let result = run(shared.clone(), stopping, receiver);
                if let Err(error) = result {
                    if let Ok(mut s) = shared.write() {
                        s.connected = false;
                        s.status = "Coletor indisponível".into();
                        s.error = Some(error);
                    }
                }
            })
            .expect("telemetry thread");
        Self {
            state,
            sender,
            stop,
        }
    }
    pub fn snapshot(&self) -> Snapshot {
        let mut value = self.state.read().map(|s| s.clone()).unwrap_or_default();
        if now_ms() - value.last_update_ms > 12_000 {
            value.connected = false;
        }
        value
    }
    pub fn load_full(&self) {
        let _ = self.sender.send(Action::Full);
    }
    pub fn load_history(&self) {
        let _ = self.sender.send(Action::History);
    }
    pub fn export(&self) {
        let _ = self.sender.send(Action::Export);
    }
}
impl Drop for Telemetry {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
    }
}

fn client() -> Result<Client, String> {
    Client::builder()
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(Duration::from_secs(2))
        .timeout(Duration::from_secs(25))
        .user_agent(concat!("RtxMonitorNative/", env!("CARGO_PKG_VERSION")))
        .build()
        .map_err(|e| e.to_string())
}
fn get(client: &Client, base: &str, path: &str) -> Result<Value, String> {
    let bytes = bytes(client, base, path)?;
    serde_json::from_slice(&bytes).map_err(|e| format!("Resposta JSON inválida: {e}"))
}
fn bytes(client: &Client, base: &str, path: &str) -> Result<Vec<u8>, String> {
    let response = client
        .get(format!("{base}{path}"))
        .send()
        .map_err(|e| e.to_string())?;
    if !response.status().is_success() {
        return Err(format!("HTTP {} em {path}", response.status()));
    }
    let mut bytes = Vec::new();
    response
        .take(MAX_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_BYTES {
        return Err("O relatório excede 32 MiB.".into());
    }
    Ok(bytes)
}

fn run(
    shared: Arc<RwLock<Snapshot>>,
    stop: Arc<AtomicBool>,
    commands: mpsc::Receiver<Action>,
) -> Result<(), String> {
    let client = client()?;
    let backend = Backend::connect(&client)?;
    shared.write().unwrap().backend_owned = backend.child.is_some();
    let base = format!("http://127.0.0.1:{}", backend.port);
    let mut last_meta = Instant::now() - Duration::from_secs(60);
    while !stop.load(Ordering::Relaxed) {
        let iteration = Instant::now();
        match collect(
            &client,
            &base,
            last_meta.elapsed() > Duration::from_secs(15),
        ) {
            Ok((gpu, public, research, windows, capabilities)) => {
                if capabilities.is_some() {
                    last_meta = Instant::now();
                }
                let mut s = shared.write().unwrap();
                if s.gpu["uuid"] != gpu["uuid"]
                    || s.gpu["driver_version"] != gpu["driver_version"]
                    || s.public["board"] != public["board"]
                {
                    s.full = None;
                    s.history = None;
                }
                s.gpu = gpu;
                s.public = public;
                s.research = research;
                s.windows = windows;
                if let Some(caps) = capabilities {
                    s.capabilities = caps;
                }
                s.connected = true;
                s.last_update_ms = now_ms();
                s.error = None;
                s.status = if s.research["state"] == "available" {
                    "Leituras atuais · somente leitura"
                } else {
                    "Coleta pública ativa · inventário privado em andamento"
                }
                .into();
                let sample = (
                    now_ms(),
                    s.metric("gpu_die_temperature_c"),
                    s.metric("gpu_hotspot_temperature_c"),
                );
                s.samples.push_back(sample);
                while s.samples.len() > 180 {
                    s.samples.pop_front();
                }
            }
            Err(error) => {
                let mut s = shared.write().unwrap();
                s.connected = false;
                s.status = "Aguardando leituras do coletor…".into();
                s.error = Some(error);
            }
        }
        while let Ok(action) = commands.try_recv() {
            let uuid = string(&shared.read().unwrap().gpu["uuid"]);
            if uuid.is_empty() {
                continue;
            }
            match action {
                Action::Full => match get(&client, &base, "/api/v1/research-telemetry") {
                    Ok(report) => {
                        let mut state = shared.write().unwrap();
                        let scoped = scope_research(report, &state.gpu, &state.public);
                        state.full = Some(Arc::new(scoped));
                    }
                    Err(e) => shared.write().unwrap().error = Some(e),
                },
                Action::History => match get(
                    &client,
                    &base,
                    &format!("/api/v1/history?gpu_uuid={uuid}&limit=100&order=desc"),
                ) {
                    Ok(report) => shared.write().unwrap().history = Some(Arc::new(report)),
                    Err(e) => shared.write().unwrap().error = Some(e),
                },
                Action::Export => {
                    let result =
                        bytes(&client, &base, &format!("/api/v1/snapshot?gpu_uuid={uuid}"))
                            .and_then(|body| {
                                let directory = data_dir().join("exports");
                                fs::create_dir_all(&directory).map_err(|e| e.to_string())?;
                                let path = directory.join(format!(
                                    "rtx3060-snapshot-{}.json",
                                    Utc::now().format("%Y%m%dT%H%M%S%.3fZ")
                                ));
                                fs::write(&path, body).map_err(|e| e.to_string())?;
                                Ok(path)
                            });
                    let mut s = shared.write().unwrap();
                    match result {
                        Ok(path) => {
                            s.export_path = Some(path);
                            s.export_error = None;
                        }
                        Err(e) => s.export_error = Some(e),
                    }
                }
            }
        }
        let delay = Duration::from_secs(2).saturating_sub(iteration.elapsed());
        for _ in 0..(delay.as_millis() / 100) {
            if stop.load(Ordering::Relaxed) {
                break;
            }
            thread::sleep(Duration::from_millis(100));
        }
    }
    drop(backend);
    Ok(())
}

fn collect(
    client: &Client,
    base: &str,
    metadata: bool,
) -> Result<(Value, Value, Value, Value, Option<Value>), String> {
    let gpus = get(client, base, "/api/v1/gpus")?;
    let matches: Vec<&Value> = gpus["gpus"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|g| g["name"].as_str().unwrap_or("").contains("RTX 3060") && g["present"] == true)
        .collect();
    if matches.len() != 1 {
        return Err("É necessário identificar uma única RTX 3060 presente; nenhuma associação por ordem é feita.".into());
    }
    let gpu = matches[0].clone();
    let uuid = gpu["uuid"].as_str().ok_or("UUID não publicado")?;
    if !uuid.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-') {
        return Err("UUID inválido no coletor".into());
    }
    let public = get(client, base, &format!("/api/v1/gpus/{uuid}/telemetry"))?;
    let research = get(client, base, "/api/v1/research-telemetry?view=summary")
        .unwrap_or_else(|e| json!({"state":"unavailable","error":e}));
    let research = scope_research(research, &gpu, &public);
    let windows = get(
        client,
        base,
        &format!("/api/v1/gpus/{uuid}/windows-telemetry"),
    )
    .unwrap_or(Value::Null);
    let capabilities = metadata
        .then(|| get(client, base, &format!("/api/v1/gpus/{uuid}/capabilities")).ok())
        .flatten();
    Ok((gpu, public, research, windows, capabilities))
}

fn scope_research(summary: Value, gpu: &Value, public: &Value) -> Value {
    if summary["collection"].is_null() {
        return summary;
    }
    let identity = &summary["identity"];
    let board = &public["board"];
    let complete = identity.is_object()
        && gpu["present"] == true
        && public["board_capture_state"] == "available"
        && board["pci_identity_available"] == true
        && board["vbios_available"] == true
        && valid_age(public["captured_at_unix_ms"].as_i64(), 12_000)
        && [
            "driver_version",
            "vbios_version",
            "pci_bus_id",
            "device_id",
            "subsystem_id",
        ]
        .iter()
        .all(|key| identity[*key].as_str().is_some_and(|v| !v.is_empty()));
    let matches = complete
        && (|| -> Option<bool> {
            let vendor = board["pci_vendor_id"].as_u64()?;
            let device = board["pci_device_id"].as_u64()?;
            let sub_vendor = board["pci_subsystem_vendor_id"].as_u64()?;
            let sub_device = board["pci_subsystem_device_id"].as_u64()?;
            if [vendor, device, sub_vendor, sub_device]
                .iter()
                .any(|v| *v > 65535)
            {
                return Some(false);
            }
            let selected_bdf = parse_bdf(board["pci_bus_id"].as_str()?)?;
            let native_bdf = (
                board["pci_domain"].as_u64()?,
                board["pci_bus"].as_u64()?,
                board["pci_device"].as_u64()?,
                board["pci_function"].as_u64()?,
            );
            let same = selected_bdf == native_bdf
                && parse_bdf(identity["pci_bus_id"].as_str()?)? == selected_bdf
                && hex(identity["device_id"].as_str()?)? == ((device << 16) | vendor)
                && hex(identity["subsystem_id"].as_str()?)? == ((sub_device << 16) | sub_vendor)
                && identity["vbios_version"]
                    .as_str()?
                    .eq_ignore_ascii_case(board["vbios_version"].as_str()?)
                && identity["driver_version"] == gpu["driver_version"]
                && public["gpu"]["driver_version"] == gpu["driver_version"]
                && public["gpu"]["uuid"] == gpu["uuid"]
                && identity.get("gpu_uuid").is_none_or(|id| id == &gpu["uuid"]);
            Some(same)
        })()
        .unwrap_or(false);
    if matches {
        return summary;
    }
    json!({"schema_version":1,"state":if complete {"identity_mismatch"} else {"identity_unavailable"},
        "identity":null,"collection":null,"observed_at":null,"updated_at":Utc::now().to_rfc3339(),
        "settings_writes_requested":false,"error":"O cache privado não corresponde à identidade confirmada da GPU selecionada."})
}

fn hex(text: &str) -> Option<u64> {
    let value = if text.get(..2).is_some_and(|p| p.eq_ignore_ascii_case("0x")) {
        &text[2..]
    } else {
        text
    };
    if value.is_empty() || value.len() > 8 {
        return None;
    }
    u64::from_str_radix(value, 16).ok()
}
fn parse_bdf(text: &str) -> Option<(u64, u64, u64, u64)> {
    let segments: Vec<_> = text.split([':', '.']).collect();
    if segments.len() != 4 {
        return None;
    }
    let value = (
        hex(segments[0])?,
        hex(segments[1])?,
        hex(segments[2])?,
        hex(segments[3])?,
    );
    (value.1 <= 255 && value.2 <= 31 && value.3 <= 7).then_some(value)
}

pub fn data_dir() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
        .join("Rtx3060Monitor")
        .join("data")
}
fn runtime_dir() -> Option<PathBuf> {
    let executable = std::env::current_exe().ok()?;
    let working = std::env::current_dir().ok()?;
    let candidates = [
        executable.parent()?.join("runtime"),
        working.join("artifacts/desktop/dist-public/runtime"),
        working.join("../artifacts/desktop/dist-public/runtime"),
        working.join("artifacts/desktop/dist-private/runtime"),
        working.join("../artifacts/desktop/dist-private/runtime"),
    ];
    candidates
        .into_iter()
        .find(|path| path.join("collector/RtxMonitor.Service.exe").is_file())
}
fn private_bundle_available(runtime: &Path) -> Result<bool, String> {
    let worker = runtime
        .join("collector/research-readers/worker.py")
        .is_file();
    let manifest = runtime
        .join("collector/research-readers/manifest.json")
        .is_file();
    let vendor = runtime.join("collector/research-readers/vendor").is_dir();
    let python = runtime.join("python/python.exe").is_file();
    if [worker, manifest, vendor, python]
        .iter()
        .any(|present| *present)
        && ![worker, manifest, vendor, python]
            .iter()
            .all(|present| *present)
    {
        return Err("O pacote privado de leitores está incompleto.".into());
    }
    Ok(worker && manifest && vendor && python)
}
pub fn self_check() -> Result<Value, String> {
    let runtime =
        runtime_dir().ok_or("Runtime local não encontrado; publique o pacote nativo completo.")?;
    let private_readers = private_bundle_available(&runtime)?;
    Ok(
        json!({"application":"RTX Monitor 3D", "version":env!("CARGO_PKG_VERSION"), "native_ui":true,"webview":false,
        "runtime":runtime, "data_directory":data_dir(), "private_readers":private_readers,
        "gpu_configuration_writes":false, "gpu_calls_by_self_check":0}),
    )
}

struct Backend {
    port: u16,
    child: Option<Child>,
    #[cfg(windows)]
    _job: Option<Job>,
}
impl Backend {
    fn connect(client: &Client) -> Result<Self, String> {
        for port in PORT..PORT + 8 {
            if is_backend(client, port) {
                return Ok(Self {
                    port,
                    child: None,
                    #[cfg(windows)]
                    _job: None,
                });
            }
        }
        let runtime = runtime_dir().ok_or(
            "O coletor local não está ativo e os arquivos de runtime não foram encontrados.",
        )?;
        let private_readers = private_bundle_available(&runtime)?;
        let port = (PORT..PORT + 8)
            .find(|p| std::net::TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, *p)).is_ok())
            .ok_or("Não há porta local livre para o coletor.")?;
        let data = data_dir();
        fs::create_dir_all(&data).map_err(|e| e.to_string())?;
        let executable = runtime.join("collector/RtxMonitor.Service.exe");
        let mut command = Command::new(&executable);
        command
            .current_dir(executable.parent().unwrap())
            .args([
                format!("--RtxMonitor:Port={port}"),
                format!(
                    "--RtxMonitor:DatabasePath={}",
                    data.join("telemetry.db").display()
                ),
                "--RtxMonitor:ProfileTelemetryEnabled=false".into(),
                format!("--RtxMonitor:ResearchTelemetryEnabled={private_readers}"),
                "--RtxMonitor:DashboardEnabled=false".into(),
                format!(
                    "--RtxMonitor:ResearchSnapshotPath={}",
                    data.join("research.json").display()
                ),
            ])
            .stdin(Stdio::null());
        if private_readers {
            command.arg(format!(
                "--RtxMonitor:ResearchPythonPath={}",
                runtime.join("python/python.exe").display()
            ));
        }
        let stamp = Utc::now().format("%Y%m%dT%H%M%S%3f").to_string();
        command
            .stdout(
                fs::File::create(data.join(format!("collector-{stamp}.log")))
                    .map_err(|e| e.to_string())?,
            )
            .stderr(
                fs::File::create(data.join(format!("collector-{stamp}.error.log")))
                    .map_err(|e| e.to_string())?,
            );
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000);
        }
        let mut child = command
            .spawn()
            .map_err(|e| format!("Não foi possível iniciar o coletor: {e}"))?;
        #[cfg(windows)]
        let job = match Job::assign(&child) {
            Ok(job) => job,
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(error);
            }
        };
        for _ in 0..80 {
            if is_backend(client, port) {
                return Ok(Self {
                    port,
                    child: Some(child),
                    #[cfg(windows)]
                    _job: Some(job),
                });
            }
            if child.try_wait().map_err(|e| e.to_string())?.is_some() {
                return Err(
                    "O coletor encerrou durante a inicialização; consulte os logs locais.".into(),
                );
            }
            thread::sleep(Duration::from_millis(250));
        }
        let _ = child.kill();
        let _ = child.wait();
        Err("O coletor não iniciou dentro do prazo.".into())
    }
}
impl Drop for Backend {
    fn drop(&mut self) {
        if let Some(child) = &mut self.child {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}
fn is_backend(client: &Client, port: u16) -> bool {
    get(client, &format!("http://127.0.0.1:{port}"), "/api")
        .is_ok_and(|value| value["service"] == "rtx-monitor")
}

#[cfg(windows)]
struct Job(windows_sys::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl Job {
    fn assign(child: &Child) -> Result<Self, String> {
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::{Foundation::CloseHandle, System::JobObjects::*};
        unsafe {
            let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if handle.is_null() {
                return Err("Não foi possível criar a sessão isolada do coletor.".into());
            }
            let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const _,
                std::mem::size_of_val(&info) as u32,
            ) == 0
                || AssignProcessToJobObject(handle, child.as_raw_handle() as _) == 0
            {
                CloseHandle(handle);
                return Err(
                    "O Windows não permitiu isolar o coletor; nenhum processo privado foi mantido."
                        .into(),
                );
            }
            Ok(Self(handle))
        }
    }
}
#[cfg(windows)]
impl Drop for Job {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn private_bundle_requires_all_components_and_public_runtime_needs_none() {
        let root = std::env::temp_dir().join(format!(
            "rtxmon-bundle-test-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let readers = root.join("collector/research-readers");
        let vendor = readers.join("vendor");
        let python = root.join("python");
        fs::create_dir_all(&readers).unwrap();
        assert_eq!(private_bundle_available(&root).unwrap(), false);
        fs::write(readers.join("worker.py"), b"test fixture").unwrap();
        assert!(private_bundle_available(&root).is_err());
        fs::write(readers.join("manifest.json"), b"test fixture").unwrap();
        fs::create_dir_all(&vendor).unwrap();
        fs::create_dir_all(&python).unwrap();
        fs::write(python.join("python.exe"), b"test fixture").unwrap();
        assert_eq!(private_bundle_available(&root).unwrap(), true);
        fs::remove_file(readers.join("worker.py")).unwrap();
        fs::remove_file(readers.join("manifest.json")).unwrap();
        fs::remove_file(python.join("python.exe")).unwrap();
        fs::remove_dir(vendor).unwrap();
        fs::remove_dir(readers).unwrap();
        fs::remove_dir(python).unwrap();
        fs::remove_dir(root.join("collector")).unwrap();
        fs::remove_dir(root).unwrap();
    }
    fn identity_fixture() -> (Value, Value, Value) {
        let gpu = json!({"uuid":"GPU-IDENTITY-TEST","driver_version":"621.01","present":true});
        let public = json!({"captured_at_unix_ms":now_ms(),"board_capture_state":"available", "gpu":gpu,
            "board":{"pci_identity_available":true,"vbios_available":true,"pci_bus_id":"0000:01:00.0",
            "pci_vendor_id":4318,"pci_device_id":9476,"pci_subsystem_vendor_id":4318,"pci_subsystem_device_id":5430,
            "pci_domain":0,"pci_bus":1,"pci_device":0,"pci_function":0,"vbios_version":"94.06.25.00.fc"}});
        let private = json!({"state":"available","identity":{"driver_version":"621.01","pci_bus_id":"00000000:01:00.0",
            "device_id":"0x250410DE","subsystem_id":"0x153610DE","vbios_version":"94.06.25.00.fc"},
            "collection":{"readings":[{"field":"gpu_hotspot_temperature_c","value":46.0}]}});
        (gpu, public, private)
    }
    #[test]
    fn windows_values_expire_and_disconnect_as_gaps() {
        let mut snapshot = Snapshot {
            connected: true,
            gpu: json!({"uuid":"GPU-WINDOWS-TEST"}),
            windows: json!({"state":"partial","gpu":{"uuid":"GPU-WINDOWS-TEST"},"captured_at_unix_ms":now_ms(),
            "local_memory":{"state":"available","value":1024,"unit":"bytes"},"engines":[{"engine_type":"3D","utilization":{"state":"available","value":12.0,"unit":"percent"}}]}),
            ..Default::default()
        };
        assert_eq!(snapshot.windows_metrics()[1].value, Some(12.0));
        snapshot.connected = false;
        assert!(snapshot
            .windows_metrics()
            .iter()
            .all(|m| m.value.is_none() && m.raw == "—"));
        snapshot.connected = true;
        snapshot.windows["captured_at_unix_ms"] = json!(now_ms() - 13000);
        assert!(snapshot.windows_metrics().iter().all(|m| m.value.is_none()));
    }
    #[test]
    fn windows_wrong_identity_and_failed_counter_are_not_live() {
        let mut snapshot = Snapshot {
            connected: true,
            gpu: json!({"uuid":"GPU-A"}),
            windows: json!({"state":"available","gpu":{"uuid":"GPU-B"},"captured_at_unix_ms":now_ms(),
            "engines":[{"engine_type":"3D","utilization":{"state":"available","value":99,"unit":"percent"}}]}),
            ..Default::default()
        };
        assert!(snapshot.windows_metrics()[0].value.is_none());
        snapshot.windows["gpu"]["uuid"] = json!("GPU-A");
        snapshot.windows["engines"][0]["utilization"]["state"] = json!("query_failed");
        assert!(snapshot.windows_metrics()[0].value.is_none());
    }
    #[test]
    fn private_cache_matches_full_selected_gpu_identity() {
        let (gpu, public, private) = identity_fixture();
        assert_eq!(scope_research(private.clone(), &gpu, &public), private);
    }
    #[test]
    fn other_board_private_cache_is_withdrawn_without_modifying_original() {
        let (gpu, public, mut private) = identity_fixture();
        private["identity"]["pci_bus_id"] = json!("00000000:02:00.0");
        let denied = scope_research(private.clone(), &gpu, &public);
        assert_eq!(denied["state"], "identity_mismatch");
        assert!(denied["collection"].is_null());
        assert_eq!(private["collection"]["readings"][0]["value"], 46.0);
    }
    #[test]
    fn unknown_or_stale_identity_never_supplies_private_values() {
        let (gpu, mut public, mut private) = identity_fixture();
        private["identity"] = Value::Null;
        assert!(scope_research(private, &gpu, &public)["collection"].is_null());
        let (_, _, private) = identity_fixture();
        public["captured_at_unix_ms"] = json!(now_ms() - 13000);
        assert!(scope_research(private, &gpu, &public)["collection"].is_null());
    }
    #[test]
    fn driver_and_subsystem_mismatch_are_not_inferred_from_card_name() {
        let (gpu, public, mut private) = identity_fixture();
        private["identity"]["driver_version"] = json!("610.88");
        assert!(scope_research(private, &gpu, &public)["collection"].is_null());
        let (_, _, mut private) = identity_fixture();
        private["identity"]["subsystem_id"] = json!("0x153710de");
        assert!(scope_research(private, &gpu, &public)["collection"].is_null());
    }
    #[test]
    fn unavailable_values_are_never_zero_filled() {
        let mut s = Snapshot {
            connected: true,
            ..Default::default()
        };
        s.public = json!({"fields":[{"field":"fan_speed_intended_rpm","provider_native_id":0,"state":"not_supported","value_u64":1234,"timestamp_unix_ms":now_ms()}]});
        assert_eq!(s.fans(), [None, None]);
        assert_eq!(s.metrics()[0].raw, "—");
    }
    #[test]
    fn expired_rpm_does_not_animate_as_a_live_reading() {
        let mut s = Snapshot {
            connected: true,
            ..Default::default()
        };
        s.public = json!({"fields":[{"field":"fan_speed_intended_rpm","provider_native_id":0,"state":"available","value_u64":1100,"timestamp_unix_ms":now_ms()-13000}]});
        assert_eq!(s.fans(), [None, None]);
    }
    #[test]
    fn zero_rpm_remains_a_valid_available_zero() {
        let mut s = Snapshot {
            connected: true,
            ..Default::default()
        };
        s.public = json!({"fields":[{"field":"fan_speed_intended_rpm","provider_native_id":0,"state":"available","value_u64":0,"timestamp_unix_ms":now_ms()}]});
        assert_eq!(s.fans(), [Some(0.0), None]);
    }
    #[test]
    fn big_integer_payload_remains_exact_without_float_conversion() {
        let v: Value = serde_json::from_str("18446744073709551615").unwrap();
        assert_eq!(v.to_string(), "18446744073709551615");
        assert_eq!(safe_f64(&v), None);
    }
    #[test]
    fn logical_thermal_channels_are_not_assigned_to_model_parts() {
        let mut s = Snapshot {
            connected: true,
            ..Default::default()
        };
        s.research = json!({"collection":{"readings":[{"field":"thermal_channel_6_temperature_c","state":"available","value":44,"unit":"°C","captured_at":Utc::now().to_rfc3339(),"max_age_ms":180000}]}});
        assert_eq!(s.metric("gpu_hotspot_temperature_c"), None);
        assert_eq!(s.metric("gpu_die_temperature_c"), None);
    }
}
