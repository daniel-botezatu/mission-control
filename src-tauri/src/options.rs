use std::path::PathBuf;

#[derive(Clone)]
pub struct Options {
    pub data: PathBuf,
    pub background: bool,
    pub mode: Option<String>,
    pub automation_port: Option<u16>,
}

impl Options {
    pub fn parse() -> Result<Self, String> {
        #[cfg(windows)]
        let default = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .map(|p| p.join("MissionControl"));
        #[cfg(target_os = "macos")]
        let default = std::env::var_os("HOME")
            .map(PathBuf::from)
            .map(|p| p.join("Library/Application Support/MissionControl"));
        #[cfg(not(any(windows, target_os = "macos")))]
        let default = None;
        let default = std::env::var_os("MISSION_CONTROL_DATA_DIR")
            .filter(|value| !value.is_empty()).map(PathBuf::from).or(default);
        let mut options = Self {
            data: default.ok_or("This platform has no supported data directory")?,
            background: false,
            mode: None,
            automation_port: None,
        };
        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--data-dir" => options.data = args.next().ok_or("Missing data directory")?.into(),
                "--background" | "--start-hidden" => options.background = true,
                "--live" => options.mode = Some("live".into()),
                "--demo" => options.mode = Some("demo".into()),
                "--automation-port" => {
                    let port = args
                        .next()
                        .ok_or("Missing automation port")?
                        .parse::<u16>()
                        .map_err(|_| "Invalid automation port")?;
                    if port < 1024 {
                        return Err("Automation port must be at least 1024".into());
                    }
                    options.automation_port = Some(port);
                }
                _ => return Err(format!("Unknown argument: {arg}")),
            }
        }
        if !options.data.is_absolute() {
            return Err("Use an absolute data directory".into());
        }
        // Do not canonicalize through a different spelling: credential identities use this path.
        let mut normalized = PathBuf::new();
        for component in options.data.components() {
            match component {
                std::path::Component::CurDir => {}
                std::path::Component::ParentDir => {
                    normalized.pop();
                }
                other => normalized.push(other.as_os_str()),
            }
        }
        options.data = normalized;
        Ok(options)
    }
}
