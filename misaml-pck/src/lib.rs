use std::{
    fs::File,
    io::{Read, Seek, SeekFrom},
    path::Path,
};

use aes::{Aes256, cipher::KeyIvInit};
use md5::{Digest, Md5};

type Aes256CfbDec = cfb_mode::Decryptor<Aes256>;

#[derive(Debug, thiserror::Error)]
pub enum PckError {
    #[error("not a valid Godot PCK file (bad magic)")]
    BadMagic,

    #[error("unsupported PCK format version: {0}")]
    UnsupportedFormat(u32),

    #[error("file too short to contain a valid header")]
    Truncated,

    #[error("no known profile could decrypt this directory")]
    NoMatchingProfile,

    #[error("directory MD5 mismatch, wrong key or corrupt data")]
    Md5Mismatch,

    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
}

// a known key pair for a specific game build if changed in a
// future update, re-derive and add it here
pub struct Profile {
    pub name: &'static str,
    pub base_key: [u8; 32],
    pub xor_mask: [u8; 32],
}

impl Profile {
    pub fn eff_key(&self) -> [u8; 32] {
        std::array::from_fn(|i| self.base_key[i] ^ self.xor_mask[i])
    }
}

pub const KNOWN_PROFILES: &[Profile] = &[Profile {
    name: "mnemonimov-build-25044332", // linux public branch
    base_key: hex32("4b90ae264d56f519eab29aff5f988024a08535eb4387954bf50a18e3e2e26a7e"),
    xor_mask: hex32("3d6cd415efd72f73d718cf8b29c0c809987355acda9bc965586282bd0dccd058"),
}];

// const hex decoder for the hardcoded keys above
const fn hex32(s: &str) -> [u8; 32] {
    let bytes = s.as_bytes();
    let mut out = [0u8; 32];
    let mut i = 0;

    while i < 32 {
        out[i] = (hex_val(bytes[i * 2]) << 4) | hex_val(bytes[i * 2 + 1]);
        i += 1;
    }

    out
}

const fn hex_val(c: u8) -> u8 {
    match c {
        b'0'..=b'9' => c - b'0',
        b'A'..=b'F' => c - b'A' + 10,
        b'a'..=b'f' => c - b'a' + 10,
        _ => panic!("invalid hex digit in KNOWN_PROFILES"),
    }
}

#[derive(Debug)]
pub struct PckHeader {
    pub format: u32,
    pub godot_version: (u32, u32, u32),
    pub flags: u32,
    pub file_base: u64,
    pub dir_offset: u64,
}

#[derive(Debug)]
pub struct PckEntry {
    pub path: String,
    pub abs_off: u64,
    pub size: u64,
    pub md5: [u8; 16],
    pub flags: u32,
}

pub struct Pck {
    pub header: PckHeader,
    pub entries: Vec<PckEntry>,
    pub matched_profile: Option<&'static Profile>,
}

// reader for parsing the seq binary data, simplified
struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }

    fn take(&mut self, len: usize) -> Result<&'a [u8], PckError> {
        let end = self.pos.checked_add(len).ok_or(PckError::Truncated)?;
        let bytes = self.data.get(self.pos..end).ok_or(PckError::Truncated)?;

        self.pos = end;
        Ok(bytes)
    }

    fn bytes<const N: usize>(&mut self) -> Result<[u8; N], PckError> {
        self.take(N)?.try_into().map_err(|_| PckError::Truncated)
    }

    fn u32(&mut self) -> Result<u32, PckError> {
        Ok(u32::from_le_bytes(self.bytes()?))
    }

    fn u64(&mut self) -> Result<u64, PckError> {
        Ok(u64::from_le_bytes(self.bytes()?))
    }

    fn skip(&mut self, len: usize) -> Result<(), PckError> {
        self.take(len)?;
        Ok(())
    }
}

fn parse_header(data: &[u8]) -> Result<(PckHeader, usize), PckError> {
    let mut reader = Reader::new(data);

    if reader.take(4)? != b"GDPC" {
        return Err(PckError::BadMagic);
    }

    let format = reader.u32()?;
    let major = reader.u32()?;
    let minor = reader.u32()?;
    let patch = reader.u32()?;
    let flags = reader.u32()?;
    let file_base = reader.u64()?;

    let dir_offset = match format {
        3 => reader.u64()?,
        2 => {
            // 16 reserved u32 fields come before the file count
            reader.skip(16 * 4)?;
            reader.pos as u64
        }
        other => return Err(PckError::UnsupportedFormat(other)),
    };

    Ok((
        PckHeader {
            format,
            godot_version: (major, minor, patch),
            flags,
            file_base,
            dir_offset,
        },
        usize::try_from(dir_offset).map_err(|_| PckError::Truncated)?,
    ))
}

fn decrypt_dir(blob: &[u8], key: &[u8; 32]) -> Result<Vec<u8>, PckError> {
    let mut reader = Reader::new(blob);

    let stored_md5 = reader.bytes::<16>()?;

    let plaintext_size = usize::try_from(reader.u64()?).map_err(|_| PckError::Truncated)?;

    let iv = reader.bytes::<16>()?;

    let encrypted_size = plaintext_size
        .checked_add(15)
        .map(|size| size & !15)
        .ok_or(PckError::Truncated)?;

    let ciphertext = reader.take(encrypted_size)?;

    let mut plaintext = ciphertext.to_owned();

    Aes256CfbDec::new(key.into(), &iv.into()).decrypt(&mut plaintext);

    plaintext.truncate(plaintext_size);

    if Md5::digest(&plaintext).as_slice() != stored_md5 {
        return Err(PckError::Md5Mismatch);
    }

    Ok(plaintext)
}

fn parse_entries(
    reader: &mut Reader<'_>,
    count: u32,
    file_base: u64,
) -> Result<Vec<PckEntry>, PckError> {
    let mut entries = Vec::with_capacity(usize::try_from(count).map_err(|_| PckError::Truncated)?);

    for _ in 0..count {
        let path_len = usize::try_from(reader.u32()?).map_err(|_| PckError::Truncated)?;

        let path = String::from_utf8_lossy(reader.take(path_len)?)
            .trim_end_matches('\0')
            .to_owned();

        let offset = reader.u64()?;
        let size = reader.u64()?;
        let md5 = reader.bytes::<16>()?;
        let flags = reader.u32()?;

        let abs_off = file_base.checked_add(offset).ok_or(PckError::Truncated)?;

        entries.push(PckEntry {
            path,
            abs_off,
            size,
            md5,
            flags,
        });
    }

    Ok(entries)
}

/// Opens and fully parses a .pck file
///
/// Tries each known profile until one produces a directory
/// with a matching MD5
pub fn open(path: impl AsRef<Path>) -> Result<Pck, PckError> {
    let data = std::fs::read(path)?;
    let (header, dir_pos) = parse_header(&data)?;

    let after_header = data.get(dir_pos..).ok_or(PckError::Truncated)?;
    let mut header_reader = Reader::new(after_header);
    let file_count = header_reader.u32()?;
    let rest = &after_header[header_reader.pos..];

    if header.flags & 1 == 0 {
        let mut reader = Reader::new(rest);
        let entries = parse_entries(&mut reader, file_count, header.file_base)?;

        return Ok(Pck {
            header,
            entries,
            matched_profile: None,
        });
    }

    let Some((profile, dir)) = KNOWN_PROFILES.iter().find_map(|profile| {
        let key = profile.eff_key();
        decrypt_dir(rest, &key).ok().map(|dir| (profile, dir))
    }) else {
        return Err(PckError::NoMatchingProfile);
    };

    let mut reader = Reader::new(&dir);
    let entries = parse_entries(&mut reader, file_count, header.file_base)?;

    Ok(Pck {
        header,
        entries,
        matched_profile: Some(profile),
    })
}

/// Extracts one entry from the .pck file.
///
/// Encrypted entries use the same format as the directory:
/// MD5 + size + IV + ciphertext.
/// Plain entries are read as-is.
pub fn extract(pck_path: &Path, entry: &PckEntry, key: &[u8; 32]) -> Result<Vec<u8>, PckError> {
    let mut file = File::open(pck_path)?;
    file.seek(SeekFrom::Start(entry.abs_off))?;

    let size = usize::try_from(entry.size).map_err(|_| PckError::Truncated)?;

    if entry.flags & 1 != 0 {
        let read_len = size.checked_add(64).ok_or(PckError::Truncated)?;

        let mut blob = vec![0; read_len];
        file.read_exact(&mut blob)?;

        decrypt_dir(&blob, key)
    } else {
        let mut data = vec![0; size];
        file.read_exact(&mut data)?;
        Ok(data)
    }
}
