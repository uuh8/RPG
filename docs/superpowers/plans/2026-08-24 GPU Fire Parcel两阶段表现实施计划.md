# GPU Fire Parcel 两阶段表现实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. 本项目当前未授权 SubAgent，除非用户针对本任务再次明确批准。

**Goal:** 保留 `MaterialId.Fire` Cell 作为 Gameplay Truth，把现有三层贴图 ParticleSystem 表现替换为 GPU Fire Parcel：生命前段形成轻薄、相连的液态火团，后段无缝扩散成气态软粒子并淡出。

**Architecture:** `Game.ElementField` 继续决定 Fire Cell、Amount、衰减与元素反应；`Game.Rendering` 以固定容量 CPU Snapshot 上传可见 Fire Cell Seed，再在 GPU 固定池中推进只读视觉 Parcel。相同 Parcel State 同时供早期 Density/Marching Cubes Surface 与后期 Gas Splat 两个 Renderer 消费，阶段权重在 40%–60% 生命周期交叉淡化；没有可交互 Gas Material，也不把 Fire 塞进 PBF Incompressibility Solver。

**Tech Stack:** Unity 6.3、URP 17、C#、Compute Shader、`GraphicsBuffer`、World-anchored 3D Density Grid、Marching Cubes、`Graphics.RenderPrimitivesIndirect`、NUnit EditMode/GPU tests、Profiler/Frame Debugger。

**Spec:** 用户于 2026-08-24 确认的“极轻液态火焰→气体→消失”视觉相变；本计划吸收完整设计，不另建 Spec。

## Global Constraints

- Gameplay Fire 仍由 `ElementWorldRuntime/IElementWorldReadOnly` 持有，Rendering 没有写权限。
- 第一阶段 Gas 仅为 Presentation，不产生 Material Cell、Status、碰撞、遮挡或 Reaction。
- 不复用 PBF Rest Density/Lambda/Collision Solver；Fire Parcel 只用 Buoyancy、Drag、Noise 与有限 Source Tether。
- 所有 Buffer 固定容量；`Update/LateUpdate` 禁止 `new`、LINQ、Boxing、Closure 和同步 GPU Readback。
- 旧 `ElementWorldFireRenderer` 保留为 Profile/Scene 可切换回滚路径，GPU Gate 完成前不删除。
- 不要求外部美术资产。颜色、Noise、Surface/Gas 混合均程序化；未来可选 RGBA Noise Texture 只能作为质量增强，不能成为首版依赖。

---

### Task 1：Fire Parcel 生命周期与 Layout Contract

**Files:**
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelPhaseMath.cs`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelGpuLayout.cs`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderProfile.cs`
- Create: `Assets/_Project/Tests/Rendering/FireParcelPhaseMathTests.cs`
- Create: `Assets/_Project/Tests/Rendering/FireParcelGpuLayoutTests.cs`

**Interfaces:**
- Consumes: `MaterialId.Fire` 与 normalized age `0..1`。
- Produces: `FireParcelPhaseSample Evaluate(float normalizedAge, in FireParcelPhaseSettings settings)`；固定 stride 的 `FireParcelSpawnGpu` 与 `FireParcelStateGpu`。

- [ ] 写 RED：0% 只有 Surface、50% Surface/Gas 同时大于 0、100% 两者为 0；所有权重单调、有限且交叉段连续。
- [ ] 运行 `Game.Rendering.Tests`，确认因类型缺失失败。
- [ ] 实现 `smoothstep` 交叉权重、Radius Growth、Alpha Fade、Buoyancy/Drag Snapshot；禁止把 Rest Density 暴露为 Fire 参数。
- [ ] 用 `Marshal.SizeOf` 锁定 C#/HLSL stride，并加入非法 NaN/负生命周期拒绝测试。
- [ ] 运行 focused tests 与 `dotnet build Game.Rendering.Tests.csproj --no-restore`。

### Task 2：Fire Cell Seed Snapshot 与固定 GPU Parcel Pool

**Files:**
- Create: `Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs`
- Create: `Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute`
- Create: `Assets/_Project/Tests/Rendering/GpuFireParcelLifecycleTests.cs`

**Interfaces:**
- Consumes: `IElementWorldReadOnly.CopyVisibleChunkKeys`、`TryGetCell`、`FireCellEmissionSampler`。
- Produces: 固定容量 `GraphicsBuffer<FireParcelStateGpu>`、active counter 与 indirect dispatch args。

- [ ] 写 RED：相同 Global Cell/Sequence 产生确定 Seed；满池使用 Ring Overwrite；失效 Cell 不立即删除已出生 Parcel，而让其完成视觉寿命。
- [ ] Renderer 初始化时一次性分配 Visible Keys、CPU Seed Array、Spawn Buffer、State Buffer、Counter/Indirect Args；视觉 Tick 只覆盖已有数组并 `SetData`。
- [ ] Compute 更新 `position += velocity*dt`；Velocity 由 World Up Buoyancy、指数 Drag、解析 Hash Noise 和早期 Source Tether 组成；Tether 在 Gas 权重上升时归零。
- [ ] Pause 时不积累追赶债务；单帧最多提交一次固定 `MaximumVisualDeltaTime`。
- [ ] GPU Test Readback 检查 finite、Age 单调、终点 inactive、Counter 不越容量；Runtime 禁止 Readback。

### Task 3：早期 Liquid-like Fire Density Surface

**Files:**
- Create: `Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute`
- Create: `Assets/_Project/Art/Elemental/Compute/FireParcelMarchingCubes.compute`
- Create: `Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSurface.shader`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceResources.cs`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceGridPlanner.cs`
- Create: `Assets/_Project/Tests/Rendering/GpuFireParcelSurfaceTests.cs`

**Interfaces:**
- Consumes: Parcel Position、Radius、Heat、SurfaceWeight；复用 Task 28/29 的 World-anchor 与局部窗口原则，但使用独立 Fire Profile/Grid 类型，避免把 Fire 参数伪装成 `LiquidRenderSettings`。
- Produces: emissive Surface Triangle Buffer 与 Indirect Draw Args。

- [ ] 写 RED：三个相邻年轻 Parcel 形成一个主要 Density Component；过渡完成后 Surface Triangle Count 归零；孤立 Parcel 不产生无限尖刺或 NaN。
- [ ] Density 使用紧支撑 Smooth Kernel 累加 `SurfaceWeight*Heat`，不执行 Lambda/Rest Density；Iso Level 只属于 Presentation Profile。
- [ ] Marching Cubes 复用既有 Case Table 与 finite/overflow 防线，但使用独立 Buffer，禁止与 Liquid Triangle Counter 混写。
- [ ] Shader 使用 HDR Core/Edge Color、Fresnel 与程序化 World-space Noise；`ZWrite Off`、Additive/Alpha Blend 由 Profile 选择，不采样外部贴图。
- [ ] Frame Debugger 验证单 Surface Draw、World Position/Clip Space 正确、Triangle Overflow=0。

### Task 4：后期 Gas Splat 与无缝交叉淡化

**Files:**
- Create: `Assets/_Project/Art/Elemental/Shaders/ElementFireGasSplat.shader`
- Extend: `Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs`
- Create: `Assets/_Project/Tests/Rendering/FireParcelGasDrawContractTests.cs`

**Interfaces:**
- Consumes: 同一 Parcel State 的 Position、Radius、Heat、GasWeight。
- Produces: Camera-facing Soft Splat Indirect Draw；不产生 Gameplay Gas。

- [ ] 写 RED：GasWeight=0 不贡献 Alpha；交叉区 SurfaceAlpha+GasAlpha 在容差内连续；过期 Parcel 不进入 Draw Count。
- [ ] Vertex Shader 用 `UNITY_MATRIX_I_V` 的 Camera Right/Up 构造 World-space Quad，Fragment 计算径向 Soft Density、Heat Gradient、Depth Fade 与程序化扰动。
- [ ] Gas Radius 随 Age 增长，Alpha/Heat 衰减；40%–60% 与 Surface 交叉，禁止硬切 Renderer。
- [ ] 使用 GPU Counter/Indirect Args，不为每 Parcel 创建 GameObject、MeshRenderer 或 Material Instance。
- [ ] 低角度、俯视、遮挡物前后验证 Depth Fade；Profiler 检查 Overdraw 与 GPU 时间。

### Task 5：数据资产、Scene 回滚 Gate 与完整解析

**Files:**
- Create through Unity: `Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset`
- Create through Unity: `Assets/_Project/Art/Elemental/Materials/M_ElementFireParcelSurface.mat`
- Create through Unity: `Assets/_Project/Art/Elemental/Materials/M_ElementFireGasSplat.mat`
- Modify through Unity: `Assets/_Project/Scenes/P7_DemoRun.unity`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: Task 1–4 完整 GPU Fire Presentation。
- Produces: P7 可切换 Legacy ParticleSystem/GPU Parcel 的人工 Gate。

- [ ] Profile 暴露 Capacity、Spawn Interval、Lifetime、Surface/Gas Transition、Buoyancy、Drag、Tether、Noise、Radius、HDR Colors、Local Bounds 与 Triangle Capacity。
- [ ] Scene 新增一个 GPU Renderer，先禁用旧 `ElementWorldFireRenderer` 但保留引用；GPU Renderer 失败时人工一键回滚旧 Renderer。
- [ ] 运行 `Game.Rendering.Tests`、相关 ElementField Reaction tests 与 `.csproj` builds；记录 Passed/Failed/Skipped，既有失败单列。
- [ ] Play Mode 验证 Fire Cell 位置一致、Water 灭火、Poison ToxicCombustion、暂停、跨 Chunk、满池、相机移动与 10 分钟运行。
- [ ] Profiler 分开记录 Seed Upload、Parcel Lifecycle、Density、MC、Surface Draw、Gas Draw、Overdraw、VRAM 和 `GC Alloc=0 B`。
- [ ] Living Explainer 按“Cell Truth→Parcel Projection→两阶段权重→两个 GPU Draw→回滚/Debug”更新，整个 Phase 在 Editor Gate 前保持“进行中”。

**建议提交序列：**

1. `feat(rendering): define gpu fire parcel lifecycle`
2. `feat(rendering): simulate fixed gpu fire parcel pool`
3. `feat(rendering): reconstruct liquid-like fire surface`
4. `feat(rendering): blend fire parcels into gas splats`
5. `feat(rendering): integrate gpu fire parcel presentation`

### Task 6：Shader Import 修复与卡通火苗密度校准

- 修复 `FireParcelDensity.compute` 使用 HLSL 冲突标识符导致 Kernel 0 无效，以及 Surface Shader 对完整 Struct 使用 Ternary 导致的类型错误。
- Renderer 在分配 Buffer 前检查完整 Kernel Contract；Import 失败时只输出一次架构诊断并停用，禁止逐帧 Dispatch Error。
- Profile 新增 Spawn Probability、统一 Radius Scale、Surface Kernel Scale、Gas Radius/Alpha、Edge Irregularity、Vertical Stretch 与 Emission Scale。
- Surface/Gas 改用普通 Alpha Blend，保留有限 HDR Bloom；Gas Billboard 使用非圆形解析边界扰动与纵向拉伸，减少可辨认的大圆片。
- 本 Task 只修改脚本/Shader；既有 Profile 会通过新增缩放字段得到安全默认值，最终美术值仍由开发者在 Inspector 人工验收。
