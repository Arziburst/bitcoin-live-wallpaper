# LockScreenProbe (spike #39, D-34 step 1)

Finds out whether an unpackaged per-user app can set the Windows lock screen image. Not part of the product.

- `--status` — read-only: Windows version, process architecture, package identity, `UserProfilePersonalizationSettings.IsSupported()`,
  `LockScreen.OriginalImageFile`, a best guess of the lock screen mode, whether a backup exists. Changes nothing.
- `--set <png>` — backs up the current image once to `%LOCALAPPDATA%\BitcoinWallpaper\lock\original.*`, copies the PNG to
  `lock\lock_<utc>.png`, calls `LockScreen.SetImageFileAsync`, on failure `UserProfilePersonalizationSettings.TrySetLockScreenImageAsync`,
  prints success or the HRESULT, reads `OriginalImageFile` back.
- `--set-twice <png>` — the same, then a second call 5 s later under a new file name (repeated refresh).
- `--restore` — sets the backed-up original back (copied under a new name first).

Every step is printed; `--set`, `--set-twice` and `--restore` also append to `lock\probe.log`.
