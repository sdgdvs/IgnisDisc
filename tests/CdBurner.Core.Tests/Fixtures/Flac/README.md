# FLAC fixtures made by the reference encoder

These files were encoded with the official xiph.org `flac.exe` 1.5.0 (Win64 build from
`https://ftp.osuosl.org/pub/xiph/releases/flac/flac-1.5.0-win.zip`) and each passes `flac -t`.
They exist so the decoder is checked against a real encoder instead of only against our own encoder.

Source audio is 6000 samples of two summed sine waves plus a little noise, with full-scale
samples every 3001 samples and a short run of digital silence (made by a throwaway script, not kept).
The MD5 in each STREAMINFO block is the one `flac.exe` computed over the original samples.

| File | Channels | Bits | Rate | Block size | flac options |
|---|---|---|---|---|---|
| flac_s16_stereo_44k_b4096.flac | 2 | 16 | 44100 | 4096 | -5 |
| flac_s16_stereo_44k_b1152.flac | 2 | 16 | 44100 | 1152 | -0 |
| flac_s16_stereo_44k_b192.flac | 2 | 16 | 44100 | 192 | -8 |
| flac_s16_mono_44k_b4608.flac | 1 | 16 | 44100 | 4608 | -8 |
| flac_s16_mono_44k_b576.flac | 1 | 16 | 44100 | 576 | -3 |
| flac_s24_stereo_48k_b4096.flac | 2 | 24 | 48000 | 4096 | -8 |
| flac_s24_stereo_48k_b1024.flac | 2 | 24 | 48000 | 1024 | -0 |
| flac_s24_mono_96k_b4096.flac | 1 | 24 | 96000 | 4096 | -5 |
