using System.Collections;
using UnityEngine;

// ClimbTestRunner scenarios: the aim marks, and sound (music volumes, the boombox, effects).
public partial class ClimbTestRunner
{
    IEnumerator Reticles()
    {
        Reticle reticle = FindFirstObjectByType<Reticle>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        CameraAim camAim = player.GetComponentInChildren<CameraAim>();
        if (reticle == null || camAim == null) { Fail("no reticle on the HUD (or no camera aim)"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        loadout.SetMainWeapon(0); // a shooter: shots leave from its muzzle
        yield return Hold(Still, false, 0.3f, "aim");
        Camera view = camAim.GetComponent<Camera>();
        if (view == null) view = camAim.GetComponentInChildren<Camera>();
        var area = (RectTransform)reticle.transform;
        Vector2 aligned = reticle.Aligned.anchoredPosition;
        float expected = Mathf.Tan(PlayerController.AimRaise * Mathf.Deg2Rad) / Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad) * area.rect.height * 0.5f;
        bool shown = reticle.Shown;
        float trueOff = Vector2.Distance(reticle.TrueMark.anchoredPosition, aligned);

        // Looking around: the ring stays put.
        player.ScriptedInput.look = new Vector2(3f, -2f);
        yield return Hold(Still, false, 0.3f, "look");
        player.ScriptedInput.look = Vector2.zero;
        yield return Hold(Still, false, 0.2f, "look");
        float ringMoved = Vector2.Distance(reticle.Aligned.anchoredPosition, aligned);
        Note($"aligned ring {aligned.y:F1} up (expected {expected:F1} for {PlayerController.AimRaise:F0}° at fov {view.fieldOfView:F0}), {aligned.x:F1} across; moved {ringMoved:F2} looking around; true mark {trueOff:F1} off it with nothing in the way");
        if (!shown) Fail("the reticles weren't shown");
        if (Mathf.Abs(aligned.y - expected) > 2f || Mathf.Abs(aligned.x) > 1f) Fail("the aligned ring isn't where the aim points");
        if (ringMoved > 0.5f) Fail("the aligned ring moved");
        if (trueOff > 1f || reticle.Blocked) Fail("the true mark wasn't on the ring with nothing in the way");

        // Something between the gun and the aim point (not in the view's way): the true mark moves onto it.
        Vector3 from = loadout.CurrentWeapon.ShotOrigin.position, target = camAim.target;
        GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = "TestReticleBlock";
        block.transform.position = Vector3.Lerp(from, target, 0.3f);
        block.transform.localScale = Vector3.one * 0.3f;
        Physics.SyncTransforms();
        yield return Hold(Still, false, 0.1f, "blocked");
        bool viewClear = Vector3.Distance(camAim.target, target) < 0.2f;
        bool blocked = reticle.Blocked;
        float shifted = Vector2.Distance(reticle.TrueMark.anchoredPosition, reticle.Aligned.anchoredPosition);
        float onBlock = Vector3.Distance(reticle.TruePoint, block.transform.position);
        Destroy(block);
        yield return Hold(Still, false, 0.1f, "clear");
        bool back = !reticle.Blocked && Vector2.Distance(reticle.TrueMark.anchoredPosition, reticle.Aligned.anchoredPosition) < 1f;
        Note($"a block between the gun and the aim point ({(viewClear ? "view clear" : "VIEW BLOCKED TOO")}): true mark {(blocked ? "on it" : "NOT moved")} ({shifted:F0} off the ring, hit {onBlock:F2}m from the block's centre), {(back ? "back on the ring" : "NOT back")} once it's gone");
        if (!viewClear) Fail("test setup: the block got in the view's way");
        else if (!blocked || shifted < 5f || onBlock > 0.3f) Fail("the true mark didn't move to what's between the gun and the aim point");
        if (!back) Fail("the true mark didn't go back to the ring");

        // Faint in swim form; hidden in a menu.
        yield return Hold(Still, true, 0.4f, "swim");
        float swimAlpha = ((CanvasGroup)reticle.GetComponent<CanvasGroup>()).alpha;
        player.ScriptedInput.swim = false;
        yield return Hold(Still, false, 0.4f, "stand");
        PauseMenu pause = FindFirstObjectByType<PauseMenu>(FindObjectsInactive.Include);
        pause.SetOpen(true);
        yield return null;
        yield return null;
        bool hiddenInMenu = !reticle.Shown;
        pause.SetOpen(false);
        yield return Hold(Still, false, 0.1f, "stand");
        Note($"in swim form alpha {swimAlpha:F2}; {(hiddenInMenu ? "hidden" : "SHOWN")} in the pause menu, {(reticle.Shown ? "back" : "NOT back")} after");
        if (swimAlpha <= 0f || swimAlpha >= 1f) Fail("the reticles weren't faint in swim form");
        if (!hiddenInMenu || !reticle.Shown) Fail("the reticles weren't hidden in the menu (and back after)");
        loadout.SetMainWeapon(weaponBefore);
    }

    IEnumerator MusicAndVolumes()
    {
        AudioManager audio = AudioManager.Instance;
        if (audio == null || audio.Library == null || audio.Library.lobby == null || audio.Library.finalMinute == null) { Fail("no audio manager (or its music library isn't set up)"); yield break; }
        float savedMusic = GameSettings.MusicVolume, savedSfx = GameSettings.SfxVolume;
        GameSettings.MusicVolume = 1f;

        // A boombox: the lobby track, partly spatial, at the music volume.
        var go = new GameObject("TestBoombox");
        go.SetActive(false);
        go.transform.position = player.transform.position + player.transform.forward * 5f;
        go.AddComponent<AudioSource>();
        Boombox box = go.AddComponent<Boombox>();
        go.SetActive(true);
        yield return Hold(Still, false, 0.3f, "listen");
        bool playing = box.Playing && box.Source.clip == audio.Library.lobby;
        float spatial = box.Source.spatialBlend, full = box.Volume;
        GameSettings.MusicVolume = 0.5f;
        yield return null;
        float half = box.Volume;
        bool nothingElse = audio.CurrentTrack == null; // free roam: no match music
        Note($"boombox: {(playing ? $"playing {box.Source.clip.name}" : "NOT playing the lobby track")}, spatial {spatial:F2}, volume {full:F2} at 100% music, {half:F3} at 50% (expected {GameSettings.Gain(0.5f):F3}: -{GameSettings.VolumeRangeDb / 2f:F0} dB); match music {(nothingElse ? "off" : "ON")} out of a match");
        if (!playing) Fail("the boombox didn't play the lobby track");
        if (spatial <= 0.05f || spatial >= 0.9f) Fail("the boombox isn't partly spatial");
        if (Mathf.Abs(full - 1f) > 0.01f || Mathf.Abs(half - GameSettings.Gain(0.5f)) > 0.001f) Fail("the boombox didn't follow the music volume (logarithmically)");
        if (!nothingElse) Fail("match music played out of a match");

        // The settings panel's sliders set the volumes.
        PauseMenu pause = FindFirstObjectByType<PauseMenu>(FindObjectsInactive.Include);
        pause.SetOpen(true);
        pause.ShowSettings();
        yield return null;
        bool hasSliders = pause.MusicSlider != null && pause.SfxSlider != null;
        if (hasSliders)
        {
            pause.MusicSlider.value = 0.25f;
            pause.SfxSlider.value = 0.4f;
        }
        yield return null;
        float music = GameSettings.MusicVolume, sfx = GameSettings.SfxVolume, boxAt = box.Volume;
        pause.SetOpen(false);

        // Effects play at the effects volume.
        AudioClip beep = AudioClip.Create("TestBeep", 4410, 1, 44100, false);
        AudioSource shot = AudioManager.PlaySfx(beep, go.transform.position, 0.5f);
        float shotVolume = shot != null ? shot.volume : -1f;
        Note($"sliders: music {music:F2}, effects {sfx:F2} (boombox now {boxAt:F3}); an effect at half volume plays at {shotVolume:F3}");
        if (!hasSliders) Fail("no music/effects volume sliders in the settings");
        else if (Mathf.Abs(music - 0.25f) > 0.01f || Mathf.Abs(sfx - 0.4f) > 0.01f || Mathf.Abs(boxAt - GameSettings.Gain(0.25f)) > 0.001f) Fail("the volume sliders didn't set the volumes");
        if (Mathf.Abs(shotVolume - 0.5f * GameSettings.Gain(0.4f)) > 0.001f) Fail("an effect didn't play at the effects volume");
        if (GameSettings.Gain(0f) != 0f || Mathf.Abs(GameSettings.Gain(0.9f) / GameSettings.Gain(0.8f) - GameSettings.Gain(0.2f) / GameSettings.Gain(0.1f)) > 1e-4f) Fail("the volume curve isn't even steps in decibels (silent at 0)");

        Destroy(go);
        GameSettings.MusicVolume = savedMusic;
        GameSettings.SfxVolume = savedSfx;
        GameSettings.Apply();
        yield return null;
    }
}
