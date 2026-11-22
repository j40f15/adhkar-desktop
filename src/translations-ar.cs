// adhkar-dua.exe, part 6: hand-made Arabic for text that machine translation gets wrong (achievement names are mostly
// puns and titles). These win over the machine translation everywhere: the library and inside Steam.
using System;
using System.Collections.Generic;

public static partial class DuaWidget {
  static Dictionary<string, string> curatedAr;

  static string CuratedAr(string en) {
    if (curatedAr == null) {
      var d = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (string line in CuratedText.Split('\n')) {
        int i = line.IndexOf('|');
        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
      }
      curatedAr = d;
    }
    string ar;
    return en != null && curatedAr.TryGetValue(en.Trim(), out ar) ? ar : null;
  }

  // Overwatch (2357570) achievement names
  const string CuratedText = @"Die Die Die... Die|مُت، مُت، مُت... مُت
Centenary|المئوية
Level 10|المستوى ١٠
Level 25|المستوى ٢٥
Level 50|المستوى ٥٠
Slice and Dice|تقطيع وتشريح
Undying|لا يموت
The Path Is Closed|الطريق مسدود
Survival Expert|خبير النجاة
Decorated|صاحب الأوسمة
Blackjack|بلاك جاك
Decked Out|كامل العدّة
Their Own Worst Enemy|ألدّ أعداء أنفسهم
Waste Not, Want Not|لا هدر ولا حاجة
Gotcha|مسكتك!
It's High Noon|إنها الظهيرة
Death From Above|الموت من السماء
Clearing the Area|تنظيف المنطقة
Rocket Man|رجل الصواريخ
Target Rich Environment|الأهداف في كل مكان
Total Recall|استرجاع كامل
Special Delivery|توصيل خاص
Triple Threat|خطر ثلاثي
Incoming!|قادم!
Simple Geometry|هندسة بسيطة
The Dragon Is Sated|التنين شبع
Mine Like a Steel Trap|لغم مثل فخ حديدي
Roadkill|دهس
Ice Blocked|محبوس في الثلج
Cold Snap|موجة برد
Raid Wipe|إبادة الغارة
Armor Up!|البس الدرع!
Did That Sting?|لسعتك؟
Smooth as Silk|ناعم كالحرير
Shot Down|إسقاط
Game Over|انتهت اللعبة
I Am Your Shield|أنا درعك
Storm, Earth and Fire|عاصفة وأرض ونار
Giving You the Hook|علقتك بالخطّاف
Hog Wild|هيجان الخنزير
Mine Sweeper|كاسحة ألغام
Anger Management|إدارة الغضب
Power Overwhelming|قوة طاغية
The Power of Attraction|قوة الجذب
The Floor Is Lava|الأرض حمم
Supersonic|أسرع من الصوت
Group Health Plan|تأمين صحي جماعي
Huge Rez|إحياء جماعي
The Car Wash|مغسلة السيارات
Huge Success|نجاح باهر
Rapid Discord|فتنة خاطفة
The Iris Embraces You|القزحية تحتضنك
Lockdown|إغلاق تام
Double Cap|استحواذ مزدوج
Escort Duty|مهمة المرافقة
Can't Touch This|ما تقدر تلمسني
Shutout|صفر للخصم
World Traveler|رحّالة العالم
The Friend Zone|منطقة الأصدقاء
Naptime|وقت القيلولة
Enabler|الداعم
Survived the Night|نجوت من الليل
Not a Scratch|ولا خدش
Four They Were|كانوا أربعة
Held The Door|صمدت عند الباب
Hack the Planet|اخترق الكوكب
Power Outage|انقطاع الكهرباء
Snowed In|محاصر بالثلج
Cool As Ice|بارد كالثلج
Whap!|طاخ!
Ambush!|كمين!
Flagbearer|حامل الراية
Cleanup Duty|مهمة التنظيف
Terrable Terrable Damage|ضرر رهيب رهيب
Tip Of The Spear|رأس الحربة
Mission Complete|اكتملت المهمة
Unit Commendation|تكريم الوحدة
Distinguished Service|خدمة متميزة
Handle With Care|تعامل بحذر
Strike Team|فرقة الهجوم
Replacements|البدلاء
Power Punch|لكمة قوية
Cratered|حفرة
Volley|وابل
Safe Hands|أيادٍ أمينة
Lúcio Hat Trick|هاتريك لوسيو
Dawn Breaks|طلوع الفجر
Six They Were|كانوا ستة
Unscathed|بلا خدش
Survivor|الناجي
Antipode|النقيض
Simple Trigonometry|حساب مثلثات بسيط
Thinking With Your Stomach|التفكير بالبطن
Yeti Catcher|صياد اليتي
Grounded|على الأرض
Excuse Me|عن إذنك
The Venice Incident|حادثة البندقية
Redacted|محجوب
Into the Shadows|إلى الظلال
A Quiet Night|ليلة هادئة
Plausible Deniability|إنكار معقول
Clean Getaway|هروب نظيف
Adaptation|التكيّف
Strike|الضربة
Six Wanderers|ستة رحّالة
Hardened Defenders|مدافعون صامدون
Dawn Patrol|دورية الفجر
Served Up|على طبق
Short Fuse|فتيل قصير
A Couple Of Flakes|كم ندفة ثلج
Hot Hand|يد ساخنة
Rampage|هيجان
Captured|تم الأسر
Delivery Interruption|تعطيل التوصيل
Storm Warning|تحذير من العاصفة
Maelstrom|الدوّامة
Tempest|الإعصار
Hurricane|الهوريكان
Freelancers|المستقلّون
Storm Rider|راكب العاصفة
Storm Chaser|مطارد العواصف
Eye of the Hurricane|عين الإعصار
Window of Opportunity|نافذة الفرصة
Stand By Me|خلّك معي
Event Horizon|أفق الحدث
Conservation of Energy|حفظ الطاقة
On The Move|على الطريق
Breached|اختراق
Threepeat|ثلاثية متتالية
Caught You!|صدتك!
Molten Cores|أنوية منصهرة
Glass Cannon|مدفع زجاجي
Surgical Strike|ضربة جراحية
Close Quarters|مسافة قريبة
Blood Moon Rising|صعود القمر الدموي
Storm Raging|عاصفة هائجة
Focused|مركّز
Adaptability|القدرة على التكيّف
Quick Brace|تحصين سريع
Frenzied Defense|دفاع محموم
Vengeful Ghost|شبح منتقم
Frenzied Stampede|تدافع محموم
Three They Were|كانوا ثلاثة
Volatile Zomnics|زومنكس متفجرة
Mystery Swap|تبديل غامض
Shocking Surprise|مفاجأة صاعقة
Thaw-Stopper|مانع الذوبان
Relentless Cold|برد لا يرحم
Snow Trouble, Friend|ثلج ومشاكل يا صديقي
Quick on the Draw|سريع السحب
Pay Up|ادفع!
Not So Fast, Outlaw|على مهلك يا خارج عن القانون
Thunderstorm|عاصفة رعدية
Bulletproof Barriers|حواجز ضد الرصاص
Sympathy Gains|مكاسب التعاطف
Purified|مُطهَّر
Yokai|يوكاي
Taking Charge|زمام المبادرة
Adrenaline Junkie|مدمن أدرينالين
Coup De Gracie|الضربة القاضية
Any Means|بأي وسيلة
Incomparable Pain|ألم لا يُضاهى
Giving Tree|شجرة العطاء
Life Weaving|نسج الحياة";
}
