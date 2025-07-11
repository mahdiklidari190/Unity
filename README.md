📘 آموزش استفاده از سیستم مدیریت دید (AdvancedVisibilityManager)

🇮🇷 فارسی | Persian

🎮 معرفی

AdvancedVisibilityManager یک سیستم پیشرفته برای مدیریت دید، LOD، فاصله، و Occlusion Culling در پروژه‌های Unity است.
این سیستم از Jobs + Burst + ComputeShader (GPU) استفاده می‌کند تا عملکرد فوق‌العاده‌ای ارائه دهد.


---

⚙️ نحوه استفاده

1. اضافه کردن اسکریپت:

فایل AdvancedVisibilityManager.cs را به پروژه خود اضافه کنید.

یک GameObject بسازید (مثلاً "VisibilityManager") و این اسکریپت را به آن اختصاص دهید.


2. تنظیمات در Inspector:

targetTag: تگ آبجکت‌هایی که باید مدیریت شوند.

customObjects: اگر نمی‌خواهید از تگ استفاده کنید، آبجکت‌ها را مستقیم اینجا اضافه کنید.

targetCamera: دوربین اصلی که باید دید را بررسی کند.

useGPUCulling: فعال کردن Culling با GPU (برای پروژه‌های سنگین).

autoLODSelection: فعال‌سازی انتخاب خودکار سطح LOD.


3. فراخوانی دستی در کد:

```AdvancedVisibilityManager.Instance.RegisterObject(myObject);```
```AdvancedVisibilityManager.Instance.UnregisterObject(myObject);```

4. ذخیره وضعیت در Editor:

روی GameObject کلیک کنید، سپس از منوی Inspector گزینه Export Current State را بزنید تا وضعیت دید در قالب فایل JSON ذخیره شود.



---

🧪 امکانات ویژه

پشتیبانی از Culling با GPU با استفاده از ComputeShader

پیش‌بینی حرکت دوربین برای فعال‌سازی هوشمند آبجکت‌ها

ثبت لاگ در فایل برای تست و دیباگ

پشتیبانی از SoA برای بهینه‌سازی Jobها



---

📝 پیش‌نیازها:

Unity 2022.3 یا بالاتر (ترجیحاً 2023+)

فعال بودن Burst Package

وجود فایل Resources/VisibilityCulling.compute برای GPU Culling



---

🇬🇧 English | انگلیسی

🎮 Overview

AdvancedVisibilityManager is a high-performance visibility system for Unity that supports distance-based culling, occlusion culling, frustum testing, LOD control, and GPU-based culling.
It uses Unity’s C# Job System, Burst Compiler, and Compute Shaders to achieve excellent runtime performance.


---

⚙️ How to Use

1. Add the Script:

Add AdvancedVisibilityManager.cs to your project.

Create a GameObject (e.g. "VisibilityManager") and attach the script.


2. Configure in Inspector:

targetTag: Tag of objects to manage automatically.

customObjects: Alternative manual object list (if not using tags).

targetCamera: The main camera used for visibility checks.

useGPUCulling: Enable GPU culling via compute shader.

autoLODSelection: Automatically manage LOD levels.


3. Call from Code:

```AdvancedVisibilityManager.Instance.RegisterObject(myObject);```
```AdvancedVisibilityManager.Instance.UnregisterObject(myObject);```

4. Export Visibility State (Editor):

Select the GameObject with the script attached, click on Export Current State to save object visibility data as a .json file.



---

🧪 Key Features:

GPU-based Occlusion Culling with Compute Shader

Predictive visibility using camera movement

File-based logging system (for QA/debugging)

High-performance SoA-based Job architecture



---

📝 Requirements:

Unity 2022.3+ (Recommended: Unity 2023 or newer)

Burst Package installed

A compute shader named Resources/VisibilityCulling.compute



---

🧾 لایسنس (License)

کد تحت لایسنس MIT منتشر شده و شما مجاز به استفاده، ویرایش و توزیع آن در پروژه‌های خود هستید.
The code is MIT licensed and free to use in personal or commercial projects.
