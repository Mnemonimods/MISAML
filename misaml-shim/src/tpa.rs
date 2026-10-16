// TPA (trusted platform assemblies) list rewriting

use std::{
    collections::HashSet,
    ffi::OsString,
    path::{Path, PathBuf},
};

// TPA entries are separated by ';' on windows and ':' everywhere else
#[cfg(target_os = "windows")]
pub const SEP: char = ';';
#[cfg(not(target_os = "windows"))]
pub const SEP: char = ':';

// one DLL to make available through TPA
#[derive(Debug, Clone)]
pub struct Assembly {
    pub path: PathBuf,
}

impl Assembly {
    pub fn file_name_os(&self) -> Option<OsString> {
        self.path.file_name().map(|n| n.to_os_string())
    }
}

// builds the replacement TRUSTED_PLATFORM_ASSEMBLIES value(s)
//
// rules:
// - original entries whose filename collides with something in `framework`
//   get dropped (games trimmed copy getting replaced)
// - never replace System.Private.CoreLib.dll from `framework`, CoreCLR's
//   already bound to its own CoreLib by the time this runs, swapping it is
//   unsupported (and we don't need it anyways)
// - dedupe by filename, first occur wins, so nothing shows up twice
//   under different paths
// - final order: surviving original entries, then framework, then payload
//   (MISAML's own dlls), game's real assemblies first, then whatever
//   we're replacing/adding
pub fn build_tpa(original: &str, framework: &[Assembly], payload: &[Assembly]) -> String {
    let framework: Vec<&Assembly> = framework
        .iter()
        .filter(|a| {
            !a.file_name_os()
                .is_some_and(|n| n.eq_ignore_ascii_case("System.Private.CoreLib.dll"))
        })
        // more framework filters can go here
        .collect();

    let framework_names: HashSet<OsString> =
        framework.iter().filter_map(|a| a.file_name_os()).collect();

    let mut seen: HashSet<OsString> = HashSet::new();
    let mut entries: Vec<String> = Vec::new();

    for entry in original.split(SEP).filter(|s| !s.is_empty()) {
        let path = Path::new(entry);
        let name = path.file_name().map(|n| n.to_os_string());

        if let Some(name) = &name {
            if framework_names.contains(name) {
                continue; // getting replaced below by the untrimmed copy
            }

            if !seen.insert(name.clone()) {
                continue; // already have this filename, skip the dupe
            }
        }

        entries.push(entry.to_string());
    }

    for asm in framework.iter().copied().chain(payload.iter()) {
        if let Some(name) = asm.file_name_os()
            && !seen.insert(name)
        {
            continue;
        }

        entries.push(asm.path.to_string_lossy().into_owned());
    }

    entries.join(&SEP.to_string())
}
