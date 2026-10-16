// decides which file opens get redirected. the hooks that actually intercept
// opens live in linux.rs (open64 today, future windows.rs would add
// CreateFileW here too)
//
// the game loads its dependencies from its own folder, and that folder only
// has trimmed framework dlls. so opens of those dlls get pointed at the
// untrimmed copies MISAML ships w/
//
// narrow on purpose, a file only gets redirected if ALL of these are true:
// 1. its dir is exactly the games own runtime dir
//    (APP_CONTEXT_BASE_DIRECTORY, grabbed once during coreclr_initialize)
// 2. its filename is one we're actually replacing
// 3. the replacement file exists

use std::{
    collections::HashMap,
    ffi::OsStr,
    path::{Path, PathBuf},
    sync::OnceLock,
};

// the game's own runtime dir, grabbed once from APP_CONTEXT_BASE_DIRECTORY
// during coreclr_initialize. only opens inside this exact dir are ever
// even considered for redirection
static GAME_RUNTIME_DIR: OnceLock<PathBuf> = OnceLock::new();

// (redirect from filename -> full replacement path), built once instead of
// rereading the framework dir on every file open
static REDIRECTS: OnceLock<HashMap<String, PathBuf>> = OnceLock::new();

pub fn set_game_runtime_dir(dir: PathBuf) {
    let _ = GAME_RUNTIME_DIR.set(dir);
}

// file names are case-insensitive on Windows
fn name_key(name: &OsStr) -> String {
    let name = name.to_string_lossy();

    if cfg!(windows) {
        name.to_ascii_lowercase()
    } else {
        name.into_owned()
    }
}

// compares dirs the way windows does, ignoring case, slash direction, a
// trailing slash and the \\?\ long-path prefix
fn normalize_windows_dir(path: &str) -> String {
    let path = path.strip_prefix(r"\\?\").unwrap_or(path);

    path.replace('/', "\\")
        .trim_end_matches('\\')
        .to_ascii_lowercase()
}

fn same_dir(a: &Path, b: &Path) -> bool {
    if cfg!(windows) {
        normalize_windows_dir(&a.to_string_lossy()) == normalize_windows_dir(&b.to_string_lossy())
    } else {
        a == b
    }
}

fn redirects() -> &'static HashMap<String, PathBuf> {
    REDIRECTS.get_or_init(|| {
        // build redirects from the framework directory
        let Some(framework_dir) = std::env::var_os("MISAML_FRAMEWORK_DIR") else {
            return HashMap::new();
        };

        let Ok(entries) = std::fs::read_dir(framework_dir) else {
            return HashMap::new();
        };

        let mut map = HashMap::new();

        for entry in entries.filter_map(Result::ok) {
            let path = entry.path();

            if !path
                .extension()
                .is_some_and(|ext| ext.eq_ignore_ascii_case("dll"))
            {
                continue;
            }

            let Some(name) = path.file_name() else {
                continue;
            };

            map.insert(name_key(name), path);
        }

        map
    })
}

// finds if "requested" should get redirected, and to where.
// lookup against the configured game runtime dir and redirect cache
pub fn redirect_target(requested: &Path) -> Option<PathBuf> {
    let game_dir = GAME_RUNTIME_DIR.get()?;

    // only redirect requests directly from the game runtime directory
    if !same_dir(requested.parent()?, game_dir) {
        return None;
    }

    redirects().get(&name_key(requested.file_name()?)).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn windows_dirs_ignore_case_slashes_prefix_and_trailing_slash() {
        let a = normalize_windows_dir(r"\\?\Z:\Home\Game\Data\");
        let b = normalize_windows_dir("z:/home/game/data");
        assert_eq!(a, b);
        assert_eq!(a, r"z:\home\game\data");
    }

    #[test]
    fn redirects_only_known_dlls_in_the_game_dir() {
        let base =
            std::env::temp_dir().join(format!("misaml-redirect-test-{}", std::process::id()));
        let framework = base.join("framework");
        let game = base.join("game");
        std::fs::create_dir_all(&framework).unwrap();
        std::fs::create_dir_all(&game).unwrap();
        std::fs::write(framework.join("System.Linq.dll"), b"x").unwrap();

        unsafe { std::env::set_var("MISAML_FRAMEWORK_DIR", &framework) };
        set_game_runtime_dir(game.clone());

        assert_eq!(
            redirect_target(&game.join("System.Linq.dll")),
            Some(framework.join("System.Linq.dll"))
        );

        assert_eq!(redirect_target(&game.join("Mnemonimov.dll")), None);
        assert_eq!(redirect_target(&base.join("System.Linq.dll")), None);

        let _ = std::fs::remove_dir_all(&base);
    }
}