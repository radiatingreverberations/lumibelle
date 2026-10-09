"""Opt-in stock-node CPU check; never submits a prompt or loads model weights.

Run with ComfyUI's Python: python check_motion_contract.py --comfy-root /path/to/ComfyUI
This complements Lumibelle's tensor-byte and graph tests; it does not assess motion quality.
"""
import argparse
import os
from pathlib import Path
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--comfy-root", type=Path, required=True)
    options = parser.parse_args()
    root = options.comfy_root.resolve(strict=True)
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    sys.argv = [str(root / "main.py"), "--cpu"]
    sys.path.insert(0, str(root))
    import comfy.options
    comfy.options.enable_args_parsing()
    import torch
    import comfy.utils
    import nodes
    from comfy_extras.nodes_lt import LTXVConcatAVLatent
    from comfy_extras.nodes_mask import MaskComposite, SolidMask
    from safetensors.torch import save_file

    video = torch.arange(1 * 24 * 52 * 2 * 4, dtype=torch.float32).reshape(1, 24, 52, 2, 4)
    audio = torch.arange(1 * 32 * 2 * 292, dtype=torch.float32).reshape(1, 32, 2, 292)
    original_video = video.clone()
    original_audio = audio.clone()
    for leading in (False, True):
        video_mask = torch.ones(52, 1, 1)
        video_mask[-12:] = 0 if leading else 1
        video_mask[:12] = 1 if leading else 0
        audio_mask = torch.ones(1, 2, 292)
        audio_mask[..., -65:] = 0 if leading else 1
        audio_mask[..., :65] = 1 if leading else 0
        held = SolidMask.execute(0.0, 65, 2).result[0]
        fresh = SolidMask.execute(1.0, 292, 2).result[0]
        assert torch.equal(MaskComposite.execute(fresh, held, 292 - 65 if leading else 0, 0, "multiply").result[0], audio_mask)
        masked_video = nodes.SetLatentNoiseMask().set_mask({"samples": video}, video_mask)[0]
        masked_audio = nodes.SetLatentNoiseMask().set_mask({"samples": audio}, audio_mask)[0]
        joined = LTXVConcatAVLatent.execute(masked_video, masked_audio).result[0]
        streams = joined["samples"].unbind()
        masks = joined["noise_mask"].unbind()
        assert torch.equal(streams[0], original_video)
        assert torch.equal(streams[1], original_audio)
        for index, (stream, mask, held, axis) in enumerate(zip(streams, masks, [12, 65], [2, 3])):
            expanded = comfy.utils.reshape_mask(mask, stream.shape)
            assert expanded.shape == stream.shape
            prefix = [slice(None)] * stream.ndim
            prefix[axis] = slice(-held, None) if leading else slice(0, held)
            tail = [slice(None)] * stream.ndim
            tail[axis] = slice(0, -held) if leading else slice(held, None)
            assert torch.count_nonzero(expanded[tuple(prefix)]) == 0
            assert torch.all(expanded[tuple(tail)] == 1)
            noise = torch.randn_like(stream)
            transported = noise * expanded + stream * (1 - expanded)
            assert torch.equal(transported[tuple(prefix)], stream[tuple(prefix)])
            print(f"{'ending' if leading else 'opening'} stream {index}: {tuple(stream.shape)}, preserved {held} temporal steps")

    # LoadLatent's format marker must prevent legacy scaling. Avoid writing into ComfyUI's inputs.
    with tempfile.TemporaryDirectory(prefix="lumibelle-motion-contract-") as temp:
        path = Path(temp) / "motion.latent"
        save_file({"latent_tensor": video, "latent_format_version_0": torch.empty(0)}, str(path))
        original_resolver = nodes.folder_paths.get_annotated_filepath
        try:
            nodes.folder_paths.get_annotated_filepath = lambda _: str(path)
            loaded = nodes.LoadLatent().load("motion.latent")[0]["samples"]
            assert torch.equal(loaded, original_video)
        finally:
            nodes.folder_paths.get_annotated_filepath = original_resolver
    from comfy_extras.nodes_minimax_h3 import MiniMaxH3AddGuide
    class GuideVAE:
        def encode(self, frames):
            assert frames.shape == (39, 32, 64, 3)
            return torch.zeros(1, 24, 12, 2, 4)
    guide = MiniMaxH3AddGuide.execute([[torch.zeros(1, 1), {}]], joined, 136,
                                     vae=GuideVAE(), image=torch.zeros(39, 32, 64, 3)).result[0]
    assert guide[0][1]["minimax_keyframes"][0]["resolved_frame_index"] == 136
    print("PASS: stock ending guide anchors 39 ordered frames at frame 136 of 175.")
    assert not torch.cuda.is_initialized()
    print("PASS: stock mask transport, context preservation, latent shapes and LoadLatent scaling (CPU).")


if __name__ == "__main__":
    main()
