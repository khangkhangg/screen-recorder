import struct, subprocess, tempfile, pathlib
here = pathlib.Path(__file__).parent
edge = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
sizes = [16, 20, 24, 32, 40, 48, 64, 256]
out = pathlib.Path(tempfile.mkdtemp())
pngs = []
for s in sizes:
    svg = here / ("app-small.svg" if s <= 24 else "app.svg")
    png = out / f"{s}.png"
    subprocess.run([edge, "--headless", "--disable-gpu", "--force-device-scale-factor=1", "--hide-scrollbars",
                    "--default-background-color=00000000", f"--window-size={s},{s}",
                    f"--screenshot={png}", svg.as_uri()], check=True, capture_output=True)
    pngs.append(png.read_bytes())
head = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries = b""
for s, p in zip(sizes, pngs):
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(p), offset)
    offset += len(p)
ico = here.parent.parent / "src/ScreenRecorder.App/Assets/app.ico"
ico.parent.mkdir(exist_ok=True)
ico.write_bytes(head + entries + b"".join(pngs))
