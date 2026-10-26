// Why the shell started and stopped, one line each, in <data>/shell-events.log. A "started" line with no exit line
// before it means the previous process was ended from outside (task kill, a closing parent job, a crash).
// Lines hold event names and numbers only, never paths or transcript content.
use std::io::Write;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

const FILE: &str = "shell-events.log";
const MAX_BYTES: u64 = 64 * 1024;

pub fn record(data: &Path, event: &str) {
    let path = data.join(FILE);
    // Stay small: past the limit, keep the newer half from a line boundary.
    if std::fs::metadata(&path).map_or(false, |meta| meta.len() > MAX_BYTES) {
        if let Ok(text) = std::fs::read_to_string(&path) {
            let _ = std::fs::write(&path, newer_half(&text));
        }
    }
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_secs());
    if let Ok(mut file) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
    {
        let _ = writeln!(file, "{} pid={} {event}", utc(seconds), std::process::id());
    }
}

fn newer_half(text: &str) -> &str {
    let mut start = text.len() / 2;
    while !text.is_char_boundary(start) {
        start += 1;
    }
    let half = &text[start..];
    half.find('\n').map_or("", |index| &half[index + 1..])
}

/// Unix seconds as "YYYY-MM-DDTHH:MM:SSZ" (days-to-civil conversion, proleptic Gregorian calendar).
fn utc(seconds: u64) -> String {
    let days = (seconds / 86_400) as i64;
    let rest = seconds % 86_400;
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 {
        month_index + 3
    } else {
        month_index - 9
    };
    let year = year_of_era + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z",
        rest / 3_600,
        rest % 3_600 / 60,
        rest % 60
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_utc_times_including_leap_days() {
        assert_eq!(utc(0), "1970-01-01T00:00:00Z");
        assert_eq!(utc(951_782_400), "2000-02-29T00:00:00Z");
        assert_eq!(utc(1_793_015_100), "2026-10-26T11:45:00Z");
    }

    #[test]
    fn trimming_keeps_whole_newer_lines() {
        let text = "first line\nsecond line\nthird line\nfourth\n";
        let kept = newer_half(text);
        assert!(text.ends_with(kept));
        assert!(kept.is_empty() || text[..text.len() - kept.len()].ends_with('\n'));
        assert!(kept.ends_with("fourth\n"));
    }

    #[test]
    fn records_append_one_line_each() {
        let directory = std::env::temp_dir().join(format!("mc-events-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        record(&directory, "started");
        record(&directory, "quit: tray menu");
        let text = std::fs::read_to_string(directory.join(FILE)).unwrap();
        let _ = std::fs::remove_dir_all(&directory);
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[1].ends_with(" quit: tray menu"));
    }
}
