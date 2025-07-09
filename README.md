
🇮🇷 فارسی: آموزش استفاده از سیستم Advanced Visibility Manager در یونیتی

AdvancedVisibilityManager یک کامپوننت بهینه‌سازی قدرتمند است که برای مدیریت دید اشیاء (Object Visibility) در بازی استفاده می‌شود و از Occlusion Culling و Distance Culling پشتیبانی می‌کند. این ابزار می‌تواند به صورت خودکار اشیاء دور یا غیرقابل‌دید را غیرفعال کند تا عملکرد بازی (FPS) بهینه شود.


---

🔧 نحوه استفاده:

1. افزودن به آبجکت

یک GameObject در صحنه انتخاب کن (مثلاً VisibilityController) و اسکریپت AdvancedVisibilityManager را به آن اضافه کن.


2. تنظیمات پایه:

targetTag: تگ اشیایی که می‌خوای بررسی بشن (مثلاً: "Enemy")

customObjects: اگر نمی‌خوای از تگ استفاده کنی، می‌تونی اشیاء خاصی رو دستی اضافه کنی.

targetLayer: لایه‌ای که آبجکت‌ها باید در آن باشند (مثلاً: فقط آبجکت‌هایی که در Enemy لایه هستند)


3. تنظیم دوربین:

targetCamera: دوربینی که برای بررسی دید استفاده می‌شود.

autoFindCamera: اگر تیک خورده باشد، از Camera.main استفاده می‌شود.


4. تنظیمات پیشرفته:

distanceBands: لیستی از فواصل (مثلاً [50, 100]) برای تعیین بُردهای دید.

useOcclusionCulling: فعال‌سازی سیستم Occlusion.

areObjectsStatic: اگر اشیاء استاتیک هستند، سرعت عملکرد بیشتر می‌شود.

changeThreshold: میزان تغییری که برای به‌روزرسانی لازم است (مقدار زیادتر = عملکرد بهتر)



---

🔁 توابع مهم:

RefreshObjects()

برای بروز رسانی دستی لیست اشیاء.


AddObject(GameObject obj)

افزودن آبجکت جدید به سیستم به صورت کدنویسی.


RemoveObject(GameObject obj)

حذف آبجکت از سیستم.



---

✅ نتیجه

اگر سیستم درست تنظیم شود، اشیاء بر اساس موقعیت دوربین و فاصله و اشیاء مانع‌دار فعال یا غیرفعال می‌شوند. این باعث افزایش چشم‌گیر FPS در صحنه‌های بزرگ می‌شود.


---

🇬🇧 English: How to Use the Advanced Visibility Manager in Unity

AdvancedVisibilityManager is a powerful optimization component that manages object visibility using Unity’s CullingGroup, Distance Culling, and optional Occlusion Culling. It helps boost performance by disabling objects that are too far or occluded from view.


---

🔧 How to Use:

1. Add to a GameObject

Create a controller GameObject (e.g., VisibilityController) and attach AdvancedVisibilityManager.cs.


2. Basic Setup:

targetTag: Tag of the objects to manage (e.g., "Enemy").

customObjects: Optional list of specific objects if you don't want to use tags.

targetLayer: Layer filter (e.g., only manage objects in the "Enemy" layer).


3. Camera Settings:

targetCamera: Camera used for visibility testing.

autoFindCamera: If true, will use Camera.main automatically.


4. Advanced Settings:

distanceBands: Array of distances (e.g., [50, 100]) defining LOD/visibility levels.

useOcclusionCulling: Enables Unity’s occlusion-based visibility check.

areObjectsStatic: Improves performance for static objects.

changeThreshold: The minimum transform change to trigger visibility update.



---

🔁 Important Methods:

RefreshObjects()

Manually refreshes the list of visible objects.


AddObject(GameObject obj)

Add a new object at runtime.


RemoveObject(GameObject obj)

Remove an object from the manager.



---

✅ Result

Once configured, your objects will be automatically culled when too far or hidden behind obstacles, leading to noticeable performance gains especially in large scenes.
