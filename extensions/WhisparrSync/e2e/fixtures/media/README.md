# acquire-media.mp4

The file the Torznab stub serves over its webseed in `acquire-import.spec.mjs`.

It is committed rather than generated at test time: the stub's image has no ffmpeg, and a payload
ffprobe cannot parse stalls the import on a header-parsing failure rather than on anything the spec
is about.

Two properties are load-bearing, and both were enforced by the instance rather than anticipated:

- It runs past the sample threshold. Under it, the import is held in `importPending` with the single
  word "Sample".
- It carries an audio track. Without one, the import is held with "No audio tracks detected".

It is still under 100 KB, because it is a black frame at five frames a second over silence.

Regenerate it with a host ffmpeg:

```sh
ffmpeg -y -f lavfi -i color=c=black:s=160x120:r=5 \
  -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 \
  -t 150 -c:v libx264 -preset ultrafast -crf 51 -pix_fmt yuv420p \
  -c:a aac -b:a 8k -movflags +faststart acquire-media.mp4
```
