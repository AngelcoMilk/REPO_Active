# REPO_Active v4.7.0

A lightweight BepInEx plugin for REPO that **remotely activates extraction points via the native ExtractionPoint.OnClick() chain**. It preserves full in閳ユ吇ame feedback (broadcast + marker + reward) while providing a stable planned order, manual control, and optional auto mode.

Are you overwhelmed by R.E.P.O.閳ユ獨 complex maps and the large number of extraction points? This mod reduces unnecessary backtracking and noticeably improves the overall flow.

## Why It Exists (Pain Points Solved)
- **Remote activation but still native**: uses the same OnClick logic as in閳ユ吇ame interaction; the result matches manual button presses.
- **Predictable order**: dynamic route planning reduces backtracking; the plan fixes the **spawn閳ユ唫earest point as the first target**, orders the rest by **nearest閳ユ唫eighbor from the player閳ユ獨 position**, and guarantees the **last target is the remaining point closest to spawn**.
- **Safety閳ユ吅irst**: will not activate a new point if another is already active.
- **Multiplayer friendly (host)**: discovery can consider all players閳?positions and is host閳ユ叧uthoritative.

## Features
- **Native activation**: reflection call to ExtractionPoint.OnClick().
- **Planned order**: first target is the extraction point closest to spawn; remaining points follow a nearest閳ユ唫eighbor order from current player position.
- **Dynamic planning**: the plan is rebuilt each time you trigger activation.
- **Safe gating**: activates only if **no extraction point is currently active**.
- **Discovery filter**: when DiscoverAllPoints=false, only discovered points are eligible.
- **Multiplayer (host)**: discovery uses **all players閳?positions** (host only).

## Keybind
- **F3**: Activate next extraction point (planned list)

## Configuration
Config file:
BepInEx\\config\\angelcomilk.repo_active.cfg

- AutoActivate (bool): Auto閳ユ叧ctivate at a fixed interval.
- ActivateNearest (KeyCode): Manual activation key (default F3).
- DiscoverAllPoints (bool): If true, treat all points as discovered.

## Manual vs Auto
- **Manual (F3)**: Runs the same planning + safety checks and activates one point.
- **Auto**: Periodically triggers the same F3 logic (no special activation path).


## Behavior Boundaries
- **Mode behavior**: Manual mode triggers only on F3; Auto mode uses its automatic trigger path. If your build is configured to ignore F3 in Auto mode, that is expected behavior.
- **Discovery behavior**: With DiscoverAllPoints=true, planning uses all extraction points immediately. With DiscoverAllPoints=false, planning only uses discovered points and may re-plan when new points are discovered.
- **Multiplayer authority**: With host validation enabled, only host-side logic can execute activation flow in multiplayer. Disabling host validation allows non-host attempts.
- **Re-plan trigger**: Re-planning is intended for mismatch/interference scenarios (actual activated point differs from expected queue target), not for every normal step.
## How It Chooses the Next Point
1. Capture **spawn position** from the first valid reference position.
2. Build the list of eligible extraction points (respects discovery filter).
3. Fix the **spawn閳ユ唫earest point as the first target**.
4. Sort the rest using **nearest閳ユ唫eighbor** from current player position.
5. If any extraction point is active, **do not activate**.

## Installation (r2modman)
1. Import the zip in r2modman.
2. Ensure the DLL is at:
   BepInEx\\plugins\\REPO_Active\\REPO_Active.dll

## Notes
- Multiplayer discovery is **host閳ユ唶ide only**. Clients do not aggregate positions.
- Discovery polling interval is fixed per round based on player count (performance閳ユ吅riendly).

## Credits
Author: **AngelcoMilk-婢垛晙濞囧Λ?*

---

# REPO_Active v4.7.0閿涘牅鑵戦弬鍥嚛閺勫函绱?
鏉╂瑦妲告稉鈧稉顏囦氦闁插繒娈?REPO BepInEx 濡紕绮嶉敍宀勨偓姘崇箖 **閸樼喓鏁?ExtractionPoint.OnClick() 闁炬崘鐭?*鏉╂粎鈻煎┑鈧ú缁樺絹閸欐牜鍋ｉ敍灞肩箽閻ｆ瑥鐣弫瀛樼埗閹村繐寮芥＃鍫礄楠炴寧鎸?+ 閺嶅洩顔?+ 婵傛牕濮抽敍澶涚礉楠炶埖褰佹笟娑毲旂€规氨娈戠憴鍕灊妞ゅ搫绨妴浣瑰閸斻劍甯堕崚鏈电瑢閸欘垶鈧鍤滈崝銊δ佸蹇嬧偓?
# 閸撳秷鈻?
娴ｇ姵妲搁崥锕€娲滄稉?`R.E.P.O.` 婢跺秵娼呴惃鍕勾閸ュ彞绗岀换浣割樋閻ㄥ嫭褰侀崣鏍仯閼板瞼鍔嶆径瀵稿剬妫版繐绱垫潻娆庨嚋濡紕绮嶉崣顖欎簰鐢喖濮担鐘插櫤鐏忔垼绐囬崶鎹愮閹峰拑绱濋獮鑸垫▔閽佹褰侀崡鍥ㄦ殻娴ｆ挻鐖堕幋蹇庣秼妤犲被鈧?
## 鐟欙絽鍠呴惃鍕閻?
- **鏉╂粎鈻煎┑鈧ú璁崇稻娴犲秳绻氶幐浣稿斧閻㈢喍缍嬫?*閿涙矮濞囬悽銊ょ瑢濞撳憡鍨欓崘鍛唉娴滄帊绔撮懛瀵告畱 OnClick 闁槒绶敍灞炬櫏閺嬫粈绗岄幍瀣З閹稿绗呴幐澶愭尦娑撯偓閼锋番鈧?- **妞ゅ搫绨崣顖烆暕閺?*閿涙艾濮╅幀浣筋潐閸掓帟鐭惧鍕剁礉閸戝繐鐨崣宥咁槻閹舵绻戦敍娑滎潐閸掓帡鈧槒绶稉鐚寸窗**閸戣櫣鏁撻悙瑙勬付鏉╂垹鍋ｉ崶鍝勭暰娑撹櫣顑囨稉鈧稉?*閿涘苯鍙炬担娆戝仯閹稿甯虹€硅泛缍嬮崜宥勭秴缂冾喖浠?*閺堚偓鏉╂垿鍋﹂幒鎺戠碍**閿涘苯鑻熺涵顔荤箽**閺堚偓閸氬簼绔存稉顏嗗仯娑撳搫澧挎担娆戝仯娑擃厽娓堕棃鐘虹箮閸戣櫣鏁撻悙?*閵?- **鐎瑰鍙忔导妯哄帥**閿涙艾缍嬪鍙夋箒閹绘劕褰囬悙鐟邦槱娴滃孩绺哄ú璁宠厬閺冭绱濇稉宥勭窗閸氼垰濮╅弬鎵畱濠碘偓濞叉眹鈧?- **婢舵矮姹夐崣瀣偨閿涘牅瀵岄張鐚寸礆**閿涙艾褰傞悳浼粹偓鏄忕帆閸欘垳绮ㄩ崥鍫熷閺堝甯虹€规湹缍呯純顕嗙礉閻㈠彉瀵岄張铏圭埠娑撯偓閻㈢喐鏅ラ妴?
## 閸旂喕鍏橀悧瑙勨偓?- **閸樼喓鏁撳┑鈧ú?*閿涙艾寮界亸鍕殶閻?ExtractionPoint.OnClick()閵?- **鐟欏嫬鍨濇い鍝勭碍**閿涙氨顑囨稉鈧稉顏嗘窗閺嶅洣璐熼崙铏规晸閻愯娓舵潻鎴犳畱閹绘劕褰囬悙鐧哥礉閸忔湹缍戦悙瑙勫瘻閻溾晛顔嶈ぐ鎾冲娴ｅ秶鐤嗛惃鍕付鏉╂垿鍋︽い鍝勭碍閹烘帒鍨妴?- **閸斻劍鈧浇顫夐崚?*閿涙碍鐦″▎陇袝閸欐垶绺哄ú濠氬厴娴兼岸鍣搁弬鎵晸閹存劘顫夐崚鎺戝灙鐞涖劊鈧?- **鐎瑰鍙忛梻鎼佹，**閿涙艾褰ч張澶婃躬**濞屸剝婀佹禒璁崇秿閹绘劕褰囬悙鐟邦槱娴滃孩绺哄ú璁宠厬**閺冭埖澧犳导姘承曢崣鎴炴煀閻ㄥ嫭绺哄ú姹団偓?- **閸欐垹骞囨潻鍥ㄦ姢**閿涙艾缍?DiscoverAllPoints=false 閺冭绱濇禒鍛嚒閸欐垹骞囬惃鍕仯閸欏倷绗屽┑鈧ú姹団偓?- **婢舵矮姹夐敍鍫滃瘜閺堢尨绱?*閿涙艾褰傞悳浼粹偓鏄忕帆娴ｈ法鏁?*閹碘偓閺堝甯虹€规湹缍呯純?*閿涘牅绮庢稉缁樻簚閿涘鈧?
## 韫囶偅宓庨柨?- **F3**閿涙碍绺哄ú璁崇瑓娑撯偓娑擃亝褰侀崣鏍仯閿涘牊瀵滅憴鍕灊閸掓銆冩い鍝勭碍閿?
## 闁板秶鐤嗛弬鍥︽
BepInEx\config\angelcomilk.repo_active.cfg

- AutoActivate閿涘潌ool閿涘绱伴幐澶婃祼鐎规岸妫块梾鏃囧殰閸斻劍绺哄ú姹団偓?- ActivateNearest閿涘湠eyCode閿涘绱伴幍瀣З濠碘偓濞茬粯瀵滈柨顕嗙礄姒涙顓?F3閿涘鈧?- DiscoverAllPoints閿涘潌ool閿涘绱伴弰顖氭儊鐟欏棔璐熼崗銊ユ禈瀹告彃褰傞悳鑸偓?
## 閹靛濮╂稉搴ゅ殰閸?- **閹靛濮╅敍鍦?閿?*閿涙矮绗岄懛顏勫З濡€崇础娴ｈ法鏁ら惄绋挎倱鐟欏嫬鍨濇稉搴＄暔閸忋劍顥呴弻銉礉娑撯偓濞嗏€冲涧濠碘偓濞茶绔存稉顏嗗仯閵?- **閼奉亜濮?*閿涙艾鎳嗛張鐔糕偓褑袝閸欐垵鎮撴稉鈧總?F3 闁槒绶敍鍫熸￥閻楄鐣╁┑鈧ú鏄忕熅瀵板嫸绱氶妴?
## 闁瀚ㄦ稉瀣╃娑擃亞鍋ｉ惃鍕偓鏄忕帆
1. 娴犲酣顩诲▎鈩冩箒閺佸牆寮懓鍐х秴缂冾喗宕熼懢?*閸戣櫣鏁撻悙鐟版綏閺?*閵?2. 閺嬪嫬缂撶粭锕€鎮庨弶鈥叉閻ㄥ嫭褰侀崣鏍仯閸掓銆冮敍鍫濆綀閸欐垹骞囨潻鍥ㄦ姢瑜板崬鎼烽敍澶堚偓?3. 閸ュ搫鐣?*閸戣櫣鏁撻悙瑙勬付鏉╂垹娈戦悙閫涜礋缁楊兛绔存稉顏嗘窗閺?*閵?4. 閸忔湹缍戦悙瑙勫瘻閻溾晛顔嶈ぐ鎾冲娴ｅ秶鐤嗘潻娑滎攽**閺堚偓鏉╂垿鍋﹂幒鎺戠碍**閵?5. 閼汇儱鍑￠張澶屽仯婢跺嫪绨┑鈧ú璁宠厬閿涘苯鍨?*娑撳秷袝閸欐垶鏌婂┑鈧ú?*閵?
## 鐎瑰顥婇敍鍧?modman閿?1. 閸?r2modman 娑擃厼顕遍崗?zip閵?2. 绾喛顓?DLL 鐠侯垰绶炴稉鐚寸窗
   BepInEx\plugins\REPO_Active\REPO_Active.dll

## 鐠囧瓨妲?- 婢舵矮姹夐崣鎴犲箛闁槒绶禒鍛躬**娑撶粯婧€缁?*閹笛嗩攽閿涘苯顓归幋椋庮伂娑撳秳绱伴懕姘値娴ｅ秶鐤嗛妴?- 閸欐垹骞囬幍顐ｅ伎闂傛挳娈ч崷銊︾槨鐏炩偓瀵偓婵妞傛导姘壌閹诡喕姹夐弫鏉挎祼鐎规熬绱濋梽宥勭秵閹嗗厴瀵偓闁库偓閵?
## 娴ｆ粏鈧?**AngelcoMilk-婢垛晙濞囧Λ?*