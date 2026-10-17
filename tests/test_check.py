from pathlib import Path

from npctalk.check import problems

ROOT = Path(__file__).resolve().parents[1]


def labels(tmp_path):
    return {what.split(" (")[0].split(":")[0] for _, what in problems(tmp_path)}


def test_clean_folder_passes(tmp_path):
    (tmp_path / "readme.md").write_text("Point NPCVOICE_URL at https://your-workspace--npcvoice-drama.modal.run\n")
    (tmp_path / "client.py").write_text('headers = {"Authorization": f"Bearer {token}"}\n')
    assert problems(tmp_path) == []


def test_audio_and_transcripts_are_blocked(tmp_path):
    (tmp_path / "voices" / "Lamar").mkdir(parents=True)
    (tmp_path / "voices" / "Lamar" / "clone.wav").write_bytes(b"RIFF")
    (tmp_path / "voices" / "Lamar" / "clone.lab").write_text("a game line")
    found = {where: what for where, what in problems(tmp_path)}
    assert "voices/Lamar/clone.wav" in found
    assert "voices/Lamar/clone.lab" in found


def test_game_audio_banks_are_blocked(tmp_path):
    names = ["Init.bnk", "Master.bank", "VO_Lamar.wem", "dialogue.fsb", "line.xwm", "speech.awc", "take.aiff"]
    for name in names:
        (tmp_path / name).write_bytes(b"x")
    assert sorted(where for where, _ in problems(tmp_path)) == names


def test_video_only_in_docs_media(tmp_path):
    (tmp_path / "docs" / "media").mkdir(parents=True)
    (tmp_path / "docs" / "media" / "demo.mp4").write_bytes(b"x")
    (tmp_path / "take1.mp4").write_bytes(b"x")
    found = [where for where, _ in problems(tmp_path)]
    assert found == ["take1.mp4"]


def test_keys_are_blocked(tmp_path):
    fal = "0123abcd-0123-4567-89ab-0123456789ab:" + "0123456789abcdef" * 2
    (tmp_path / "config.ini").write_text(f"ApiKey = {fal}\nCerebras = csk-{'a1' * 20}\n")
    (tmp_path / ".env").write_text("FAL_KEY=x\n")
    found = labels(tmp_path)
    assert {"fal key", "Cerebras key", ".env file"} <= found


def test_real_modal_url_is_blocked_placeholder_is_not(tmp_path):
    (tmp_path / "a.ini").write_text("Url = acme-games" + "--npcvoice-drama.us-east.modal.run\n")  # split so this file passes
    (tmp_path / "b.md").write_text("https://your-workspace--npcvoice-drama.modal.run and <workspace>--npcvoice-drama.modal.run\n")
    found = problems(tmp_path)
    assert [where for where, _ in found] == ["a.ini:1"]


def test_this_repo_ships_nothing_it_shouldnt():
    assert problems(ROOT) == []
