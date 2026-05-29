# Licensing

This document summarizes Pixcribe's project license status and the license obligations that come from bundled or referenced third-party components.

## Pixcribe Project License

Pixcribe application code is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

Apache-2.0 generally allows use, copying, modification, distribution, sublicensing, and commercial use, provided license, copyright, notice, and modification marking requirements are followed. It also includes an express patent license and patent termination provisions.

## Third-Party Dependencies

Pixcribe references the following NuGet packages:

| Component | Version | License | Purpose |
|---|---:|---|---|
| `OpenCvSharp4` | `4.13.0.20260427` | Apache-2.0 | .NET wrapper for OpenCV used for face detection support. |
| `OpenCvSharp4.runtime.win` | `4.13.0.20260302` | Apache-2.0 | Windows native runtime package used by OpenCvSharp. |

Apache-2.0 generally allows use, modification, distribution, and private or commercial use, provided copyright, license, and notice requirements are preserved. See the package metadata and the Apache-2.0 license text for exact terms.

## Bundled OpenCV Haar Cascade

Pixcribe bundles:

```text
assets/haarcascade_frontalface_default.xml
```

This file is the OpenCV frontal-face Haar cascade used by `--faceextract`. The file includes its own license notice from the Open Source Computer Vision Library / Intel license.

Important redistribution conditions include:

- Source redistributions must retain the copyright notice, conditions, and disclaimer.
- Binary redistributions must reproduce the copyright notice, conditions, and disclaimer in documentation or other distribution materials.
- Intel Corporation's name may not be used to endorse derived products without prior written permission.

The full notice is embedded at the top of `assets/haarcascade_frontalface_default.xml`.

## Model Licenses

Pixcribe can call locally installed Ollama models, but the models are not bundled in this repository.

Users are responsible for checking and complying with the license of any model they install and use, such as `moondream`, `llama3.2-vision`, `granite3.2-vision`, `llava`, or other Ollama models.

## Runtime Requirements

Pixcribe targets .NET and uses the .NET SDK/runtime to build and run. The .NET SDK/runtime has its own Microsoft licensing terms. Pixcribe does not redistribute the .NET SDK in this repository.
