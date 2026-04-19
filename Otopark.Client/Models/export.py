import torch
from pathlib import Path

# torch.load'u weights_only=False ile zorla
_original_torch_load = torch.load

def patched_torch_load(*args, **kwargs):
    kwargs["weights_only"] = False
    return _original_torch_load(*args, **kwargs)

torch.load = patched_torch_load

from ultralytics import YOLO

pt_path = Path(r"C:\Users\mboy\source\repos\Otopark\Otopark.Client\Models\license_plate_detector.pt")

if not pt_path.exists():
    raise FileNotFoundError(f"Model bulunamadı: {pt_path}")

model = YOLO(str(pt_path))

result = model.export(
    format="onnx",
    imgsz=640,
    opset=12,
    simplify=True
)

print("ONNX export tamamlandı")
print("Çıktı:", result)