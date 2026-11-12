# Third-party notices

## MIT KEMAR HRTF measurements

`src/Soundstage.Engine/Assets/mit_kemar_diffuse.hrir` is a repacked copy of the
diffuse-field equalized HRTF set (`diffuse.zip`) from:

> Bill Gardner and Keith Martin, "HRTF Measurements of a KEMAR Dummy-Head
> Microphone", MIT Media Lab Perceptual Computing Technical Report #280, 1994.
> https://sound.media.mit.edu/resources/KEMAR.html

The data is Copyright 1994 by the MIT Media Laboratory. Its README states: "It
is provided free with no restrictions on use, provided the authors are cited
when the data is used in any research or commercial application."

The samples are unchanged; only the container differs (one binary file instead
of 368 WAV files). `soundstage-cli --pack-hrtf diffuse.zip <out>` regenerates it.

## sony-head-tracker

Soundstage runs [sony-head-tracker](https://github.com/NicholasSlattery/sony-head-tracker)
by Nicholas Slattery (MIT License) as a separate program to read the headphone's
motion sensor. It is not included in this repository.

## Sony headphone control protocol

Soundstage talks to the headphones' control channel with its own code
(`src/Soundstage.Engine/SonyLink.cs`, `SonyHeadphones.cs`). The message framing
and layouts were learned from these MIT-licensed projects, which documented the
protocol:

- [SonyHeadphonesClient](https://github.com/mos9527/SonyHeadphonesClient) by
  mos9527 and contributors (originally by Plutoberth): protocol tables,
  including the WH-1000XM6 noise-control layout.
- [sony-device-center](https://github.com/marconvcm/sony-device-center) by
  marconvcm and contributors: confirmation of the ten-band equalizer format.
- [Gadgetbridge](https://codeberg.org/Freeyourgadget/Gadgetbridge) (AGPL):
  consulted as documentation of the framing only; no code was used.

## Structural head model

The optional simple renderer follows C. P. Brown and R. O. Duda, "A structural
model for binaural sound synthesis", IEEE Transactions on Speech and Audio
Processing, vol. 6, no. 5, 1998.
