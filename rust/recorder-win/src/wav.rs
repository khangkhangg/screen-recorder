use std::fs::File;
use std::io::{self, Seek, SeekFrom, Write};
use std::path::Path;

use crate::error::{Error, Result};

const RATE: u32 = 48_000;
const FRAME_BYTES: u32 = 8;

/// Writes 48 kHz stereo 32-bit float samples to a WAV file.
pub struct WavWriter {
    file: File,
    data_bytes: u64,
    header_bytes: u64,
}

impl WavWriter {
    pub fn create(path: &Path) -> Result<WavWriter> {
        let file = File::create_new(path).map_err(Error::Io)?;
        let mut writer = WavWriter { file, data_bytes: 0, header_bytes: 0 };
        writer.write_header().map_err(Error::Io)?;
        Ok(writer)
    }

    pub fn write(&mut self, samples: &[f32]) -> Result<()> {
        let bytes: Vec<u8> = samples.iter().flat_map(|s| s.to_le_bytes()).collect();
        self.file.write_all(&bytes).map_err(Error::Io)?;
        self.data_bytes += bytes.len() as u64;
        // Rewrites the sizes once per second of sound, so a file cut off by a crash still opens.
        if self.data_bytes - self.header_bytes >= (RATE * FRAME_BYTES) as u64 {
            self.write_header().map_err(Error::Io)?;
        }
        Ok(())
    }

    pub fn finish(&mut self) -> Result<()> {
        self.write_header().map_err(Error::Io)
    }

    fn write_header(&mut self) -> io::Result<()> {
        // shortcut: sizes are 32-bit, so files past 4 GB (about 3.1 hours) overflow, switch to RF64 when longer recordings are needed.
        let data = self.data_bytes as u32;
        let mut h = Vec::with_capacity(44);
        h.extend_from_slice(b"RIFF");
        h.extend_from_slice(&data.wrapping_add(36).to_le_bytes());
        h.extend_from_slice(b"WAVEfmt ");
        h.extend_from_slice(&16u32.to_le_bytes());
        h.extend_from_slice(&3u16.to_le_bytes()); // IEEE float
        h.extend_from_slice(&2u16.to_le_bytes());
        h.extend_from_slice(&RATE.to_le_bytes());
        h.extend_from_slice(&(RATE * FRAME_BYTES).to_le_bytes());
        h.extend_from_slice(&(FRAME_BYTES as u16).to_le_bytes());
        h.extend_from_slice(&32u16.to_le_bytes());
        h.extend_from_slice(b"data");
        h.extend_from_slice(&data.to_le_bytes());
        self.file.seek(SeekFrom::Start(0))?;
        self.file.write_all(&h)?;
        self.file.seek(SeekFrom::End(0))?;
        self.header_bytes = self.data_bytes;
        Ok(())
    }
}

impl Drop for WavWriter {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}
