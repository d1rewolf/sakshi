# Packaging assets

`sakshi.svg` is the logo: an open eye whose pupil is the record light (Sakshi, from Sanskrit *sākṣī*, "the
witness"). `sakshi-dark.svg` is the same mark for dark backgrounds; the README shows whichever suits the reader's
theme. The tray icon in `src/Sakshi.App/Tray/Icons.cs` draws the same eye pixel by pixel, without the iris ring, and
colours the pupil by state.

`sakshi-icon.svg` is the app icon (installers, launcher, menu entry): the logo on a light rounded tile, so it reads
on dark and light launchers alike. `sakshi.png` (512x512) and `sakshi.ico` (16, 24, 32, 48, 64, 128, 256 px) are
rendered from it and committed. After changing it, regenerate both with:

    rsvg-convert -w 512 -h 512 packaging/sakshi-icon.svg | magick - PNG32:packaging/sakshi.png
    magick packaging/sakshi.png -background none -define icon:auto-resize=16,24,32,48,64,128,256 packaging/sakshi.ico

`sakshi.icns` is not committed. The macOS job in `.github/workflows/release.yml` builds it with the tools that ship
with macOS, so no Linux icns tool is needed:

    mkdir sakshi.iconset
    for s in 16 32 128 256 512; do
      sips -z $s $s packaging/sakshi.png --out sakshi.iconset/icon_${s}x${s}.png
      sips -z $((s*2)) $((s*2)) packaging/sakshi.png --out sakshi.iconset/icon_${s}x${s}@2x.png
    done
    iconutil -c icns sakshi.iconset -o packaging/sakshi.icns

(The 512@2x entry upscales the 512 px source to 1024 px, so it is a little soft.)
