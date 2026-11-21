# Packaging assets

`sakshi.png` is the app icon: 512x512, transparent background. It is the idle tray glyph from
`src/Sakshi.App/Tray/Icons.cs` (a flat #808088 dot) enlarged to fill more of the canvas, because a launcher
icon needs more presence than a 32 px tray icon (dot radius 192 of 512 px, against 8 of 32 px in the tray).
Regenerate it with:

    magick -size 512x512 xc:none -fill '#808088' -draw 'circle 256,256 256,64' PNG32:packaging/sakshi.png

`sakshi.ico` (16, 24, 32, 48, 64, 128, 256 px) is committed. Regenerate it with:

    magick packaging/sakshi.png -background none -define icon:auto-resize=16,24,32,48,64,128,256 packaging/sakshi.ico

`sakshi.icns` is not committed. The macOS job in `.github/workflows/release.yml` builds it with the tools that ship
with macOS, so no Linux icns tool is needed:

    mkdir sakshi.iconset
    for s in 16 32 128 256 512; do
      sips -z $s $s packaging/sakshi.png --out sakshi.iconset/icon_${s}x${s}.png
      sips -z $((s*2)) $((s*2)) packaging/sakshi.png --out sakshi.iconset/icon_${s}x${s}@2x.png
    done
    iconutil -c icns sakshi.iconset -o packaging/sakshi.icns

(The 512@2x entry upscales the 512 px source to 1024 px; fine for a flat dot.)
