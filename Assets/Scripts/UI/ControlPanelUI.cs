using UnityEngine;

public class ControlPanelUI : MonoBehaviour
{
    [Header("References — assign in Inspector")]
    public SphericalPendulum pendulum;
    public PBFSolver solver;
    public BucketHole bucketHole;

    [Header("Panel Settings")]
    public KeyCode toggleKey = KeyCode.Tab;
    public bool visible = true;

    Rect windowRect = new Rect(10, 10, 430, 620);
    Vector2 scroll;

    string[] tabNames = { "🌀 Swing", "🪣 Bucket", "🎨 Paint", "🧪 Mixing", "💧 Colors", "🖼 Canvas", "📊 Report", "🔍 Compare" };
    int currentTab = 0;


    float pendingThetaDeg;
    float pendingPhiDeg;
    int pendingSwings;
    float pendingThetaVel0;
    float pendingPhiVel0;

    GUIStyle headerStyle;
    GUIStyle sectionBoxStyle;
    GUIStyle labelStyle;
    GUIStyle buttonStyle;
    GUIStyle activeButtonStyle;
    GUIStyle tabStyle;
    GUIStyle activeTabStyle;
    GUIStyle sliderLabelStyle;
    GUIStyle toggleStyle;
    Texture2D sectionBg;
    Texture2D whiteTex;
    Texture2D tabBg;
    Texture2D activeTabBg;
    Texture2D goldLineTex;
    Texture2D windowBg;
    GUIStyle windowStyle;
    bool stylesReady = false;

    void Start()
    {
        windowRect = new Rect(Screen.width - windowRect.width - 20, 10, windowRect.width, windowRect.height);

        if (pendulum != null)
        {
            pendingThetaDeg = pendulum.thetaDeg;
            pendingPhiDeg = pendulum.phiDeg;
            pendingSwings = pendulum.n_swings;
            pendingThetaVel0 = pendulum.thetaVel0;
            pendingPhiVel0 = pendulum.phiVel0;
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            visible = !visible;
    }

    Texture2D MakeTex(Color c)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, c);
        tex.Apply();
        return tex;
    }

    void BuildStyles()
    {
        sectionBg = MakeTex(new Color(1f, 1f, 1f, 0.06f));
        whiteTex = MakeTex(Color.white);
        tabBg = MakeTex(new Color(1f, 1f, 1f, 0.05f));
        activeTabBg = MakeTex(new Color(1f, 0.6f, 0.15f, 0.85f));
        goldLineTex = MakeTex(new Color(1f, 0.82f, 0.35f, 0.9f));
        windowBg = MakeTex(new Color(0.15f, 0.15f, 0.15f, 0.55f));

        windowStyle = new GUIStyle(GUI.skin.window)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(8, 8, 22, 8)
        };
        var titleColor = new Color(1f, 0.82f, 0.35f);
        windowStyle.normal.background = windowBg; windowStyle.normal.textColor = titleColor;
        windowStyle.onNormal.background = windowBg; windowStyle.onNormal.textColor = titleColor;
        windowStyle.focused.background = windowBg; windowStyle.focused.textColor = titleColor;
        windowStyle.onFocused.background = windowBg; windowStyle.onFocused.textColor = titleColor;
        windowStyle.hover.background = windowBg; windowStyle.hover.textColor = titleColor;
        windowStyle.onHover.background = windowBg; windowStyle.onHover.textColor = titleColor;
        windowStyle.active.background = windowBg; windowStyle.active.textColor = titleColor;
        windowStyle.onActive.background = windowBg; windowStyle.onActive.textColor = titleColor;

        headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(1f, 0.82f, 0.35f) }
        };

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.white }
        };

        sliderLabelStyle = new GUIStyle(labelStyle) { fontSize = 11 };

        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            padding = new RectOffset(8, 8, 4, 4)
        };
        buttonStyle.normal.textColor = Color.white;

        activeButtonStyle = new GUIStyle(buttonStyle);
        activeButtonStyle.normal.textColor = new Color(0.4f, 1f, 0.6f);
        activeButtonStyle.fontStyle = FontStyle.Bold;

        toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 12 };
        toggleStyle.normal.textColor = Color.white;

        tabStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            fixedHeight = 26,
            normal = { background = tabBg, textColor = new Color(0.8f, 0.8f, 0.8f) }
        };

        activeTabStyle = new GUIStyle(tabStyle);
        activeTabStyle.normal.background = activeTabBg;
        activeTabStyle.normal.textColor = Color.white;
        activeTabStyle.fontStyle = FontStyle.Bold;

        sectionBoxStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = sectionBg },
            padding = new RectOffset(10, 10, 8, 8),
            margin = new RectOffset(0, 0, 4, 4)
        };

        stylesReady = true;
    }

    void OnGUI()
    {
        if (!visible) return;
        if (!stylesReady) BuildStyles();

        windowRect = GUILayout.Window(12345, windowRect, DrawWindow, "Control Panel  (Tab to hide)", windowStyle);
    }

    void DrawWindow(int id)
    {
 
        GUI.color = Color.white;

        DrawTabBar();
        DrawGoldLine();
        GUILayout.Space(6);

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Width(410), GUILayout.Height(510));

        switch (currentTab)
        {
            case 0: DrawPendulumSection(); DrawEnvironmentSection(); break;
            case 1: DrawBucketSection(); break;
            case 2: DrawPaintTypeSection(); DrawSurfaceTypeSection(); DrawBrushSection(); break;
            case 3: DrawMixingSection(); DrawCohesionSection(); DrawSloshingSection(); break;
            case 4: DrawColorsSection(); break;
            case 5: DrawCanvasSection(); DrawParticleCountSection(); break;
            case 6: DrawReportSection(); break;
            case 7: DrawCompareSection(); break;
        }

        GUILayout.EndScrollView();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    void DrawSectionHeader(string title)
    {
        Color accent = new Color(1f, 0.7f, 0.25f);
        Color old = GUI.color;
        try
        {
            GUI.color = accent;
            GUILayout.Box(whiteTex, GUILayout.Height(3), GUILayout.ExpandWidth(true));
        }
        finally { GUI.color = old; }

        GUILayout.Space(3);
        GUILayout.Label(title, headerStyle);
    }

    void DrawGoldLine()
    {
        GUILayout.Box(goldLineTex, GUILayout.Height(2), GUILayout.ExpandWidth(true));
    }

    void DrawTabBar()
    {
        GUILayout.BeginHorizontal();
        for (int i = 0; i < 4; i++)
            DrawTabButton(i);
        GUILayout.EndHorizontal();

        GUILayout.Space(3);

        GUILayout.BeginHorizontal();
        for (int i = 4; i < tabNames.Length; i++)
            DrawTabButton(i);
        GUILayout.EndHorizontal();
    }

    void DrawTabButton(int i)
    {
        bool isActive = currentTab == i;
        if (GUILayout.Button(tabNames[i], isActive ? activeTabStyle : tabStyle))
            currentTab = i;
    }

    void DrawPendulumSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("SWING SETUP");
        if (pendulum == null) { GUILayout.Label("(No Pendulum assigned)", labelStyle); GUILayout.EndVertical(); return; }

        GUILayout.Label($"Start angle (theta): {pendingThetaDeg:F0}°", sliderLabelStyle);
        pendingThetaDeg = GUILayout.HorizontalSlider(pendingThetaDeg, 0.01f, 179f);

        GUILayout.Label($"Direction (phi): {pendingPhiDeg:F0}°", sliderLabelStyle);
        pendingPhiDeg = GUILayout.HorizontalSlider(pendingPhiDeg, 0f, 360f);

        GUILayout.Label($"Target swings: {pendingSwings}", sliderLabelStyle);
        pendingSwings = Mathf.RoundToInt(GUILayout.HorizontalSlider(pendingSwings, 1, 60));

        GUILayout.Label($"Initial theta velocity: {pendingThetaVel0:F2} rad/s", sliderLabelStyle);
        pendingThetaVel0 = GUILayout.HorizontalSlider(pendingThetaVel0, -3f, 3f);

        GUILayout.Label($"Initial phi velocity: {pendingPhiVel0:F2} rad/s", sliderLabelStyle);
        pendingPhiVel0 = GUILayout.HorizontalSlider(pendingPhiVel0, -3f, 3f);

        GUILayout.Space(4);
        if (GUILayout.Button("▶  Launch Swing", buttonStyle))
        {
            pendulum.thetaDeg = pendingThetaDeg;
            pendulum.phiDeg = pendingPhiDeg;
            pendulum.n_swings = pendingSwings;
            pendulum.thetaVel0 = pendingThetaVel0;
            pendulum.phiVel0 = pendingPhiVel0;
            pendulum.ResetSimulation();
        }
        GUILayout.EndVertical();
    }

    void DrawEnvironmentSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("ENVIRONMENT  (live)");
        if (pendulum == null) { GUILayout.EndVertical(); return; }

        GUILayout.Label($"Gravity: {pendulum.gravity:F2} m/s²", sliderLabelStyle);
        pendulum.gravity = GUILayout.HorizontalSlider(pendulum.gravity, 1f, 20f);

        GUILayout.Label($"Joint friction: {pendulum.jointFriction:F3}", sliderLabelStyle);
        pendulum.jointFriction = GUILayout.HorizontalSlider(pendulum.jointFriction, 0f, 1f);

        GUILayout.Label($"Humidity: {pendulum.humidity:F2}", sliderLabelStyle);
        pendulum.humidity = GUILayout.HorizontalSlider(pendulum.humidity, 0f, 1f);

        GUILayout.Label($"Air drag: {pendulum.dragCoefficient:F2}", sliderLabelStyle);
        pendulum.dragCoefficient = GUILayout.HorizontalSlider(pendulum.dragCoefficient, 0f, 1.5f);

  
        GUILayout.Space(6);
        Vector3 wind = pendulum.windVelocity;

        GUILayout.Label($"Wind X: {wind.x:F1} m/s", sliderLabelStyle);
        wind.x = GUILayout.HorizontalSlider(wind.x, -10f, 10f);

        GUILayout.Label($"Wind Z: {wind.z:F1} m/s", sliderLabelStyle);
        wind.z = GUILayout.HorizontalSlider(wind.z, -10f, 10f);

        pendulum.windVelocity = wind;

        if (GUILayout.Button("No Wind (0, 0)", buttonStyle))
            pendulum.windVelocity = Vector3.zero;

        if (pendulum.dragCoefficient <= 0.01f)
            GUILayout.Label("(wind has no effect while Air drag = 0)", sliderLabelStyle);

        GUILayout.EndVertical();
    }

    void DrawPaintTypeSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("PAINT TYPE");
        if (solver == null) { GUILayout.Label("(No Solver assigned)", labelStyle); GUILayout.EndVertical(); return; }

        GUILayout.Label("Current: " + solver.paintType, labelStyle);

        int col = 0;
        GUILayout.BeginHorizontal();
        foreach (PBFSolver.PaintType t in System.Enum.GetValues(typeof(PBFSolver.PaintType)))
        {
            bool isActive = solver.paintType == t;
            if (GUILayout.Button(t.ToString(), isActive ? activeButtonStyle : buttonStyle))
            {
                solver.paintType = t;
                solver.ApplyPaintType(); 
            }
            col++;
            if (col % 2 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    void DrawBucketSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("BUCKET");
        if (pendulum == null) { GUILayout.Label("(No Pendulum assigned)", labelStyle); GUILayout.EndVertical(); return; }

        GUILayout.Label($"Bucket mass: {pendulum.bucketMass:F2} kg", sliderLabelStyle);
        pendulum.bucketMass = GUILayout.HorizontalSlider(pendulum.bucketMass, 0.1f, 5f);

        GUILayout.Label($"Bucket radius: {pendulum.bucketRadius:F2} m", sliderLabelStyle);
        pendulum.bucketRadius = GUILayout.HorizontalSlider(pendulum.bucketRadius, 0.05f, 0.5f);

        GUILayout.Label($"Fluid mass: {pendulum.fluidMass:F2} kg", sliderLabelStyle);
        pendulum.fluidMass = GUILayout.HorizontalSlider(pendulum.fluidMass, 0.05f, 3f);

        if (bucketHole != null)
        {
            GUILayout.Label($"Exit hole diameter: {bucketHole.holeDiameter:F3} m", sliderLabelStyle);
            bucketHole.holeDiameter = GUILayout.HorizontalSlider(bucketHole.holeDiameter, 0.005f, 0.15f);

            GUILayout.Label($"Paint flow rate: {bucketHole.particlesPerSecond:F1} particles/s", sliderLabelStyle);
            bucketHole.particlesPerSecond = GUILayout.HorizontalSlider(bucketHole.particlesPerSecond, 1f, 100f);
        }
        else
        {
            GUILayout.Label("(No BucketHole assigned — hole size / flow rate hidden)", labelStyle);
        }
        GUILayout.EndVertical();

        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("SUSPENSION  (needs Launch Swing to fully apply)");

        GUILayout.Label($"Rope length: {pendulum.restLength:F2} m", sliderLabelStyle);
        pendulum.restLength = GUILayout.HorizontalSlider(pendulum.restLength, 0.3f, 3f);

        GUILayout.Label($"Rope stiffness: {pendulum.ropeStiffness:F0}", sliderLabelStyle);
        pendulum.ropeStiffness = GUILayout.HorizontalSlider(pendulum.ropeStiffness, 50f, 20000f);

        GUILayout.Label("Pivot point (X, Y, Z):", sliderLabelStyle);
        Vector3 p = pendulum.pivotPosition;
        p.x = GUILayout.HorizontalSlider(p.x, -3f, 3f);
        p.y = GUILayout.HorizontalSlider(p.y, 0f, 6f);
        p.z = GUILayout.HorizontalSlider(p.z, -3f, 3f);
        pendulum.pivotPosition = p;
        GUILayout.EndVertical();
    }
    void DrawCanvasSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("CANVAS  (live)");
        if (solver == null) { GUILayout.EndVertical(); return; }

        solver.canvasIsHorizontal = GUILayout.Toggle(
            solver.canvasIsHorizontal, solver.canvasIsHorizontal ? " Horizontal" : " Vertical", toggleStyle);

        if (solver.canvasTransform != null)
        {
            Vector3 s = solver.canvasTransform.localScale;
            GUILayout.Label($"Canvas width scale: {s.x:F2}", sliderLabelStyle);
            s.x = GUILayout.HorizontalSlider(s.x, 0.2f, 5f);
            GUILayout.Label($"Canvas depth scale: {s.z:F2}", sliderLabelStyle);
            s.z = GUILayout.HorizontalSlider(s.z, 0.2f, 5f);
            solver.canvasTransform.localScale = s;

            GUILayout.Space(6);
            DrawSectionHeader("CANVAS POSITION  (live)");
            Vector3 pos = solver.canvasTransform.localPosition;
            GUILayout.Label($"Canvas Y position / near-far: {pos.y:F2}", sliderLabelStyle);
            pos.y = GUILayout.HorizontalSlider(pos.y, -5f, 5f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Y - 0.25", buttonStyle)) pos.y -= 0.25f;
            if (GUILayout.Button("Y + 0.25", buttonStyle)) pos.y += 0.25f;
            if (GUILayout.Button("Reset Y", buttonStyle)) pos.y = 0f;
            GUILayout.EndHorizontal();

            solver.canvasTransform.localPosition = pos;

            GUILayout.Space(6);
            DrawSectionHeader("CANVAS PROJECTION  (live)");
            solver.useTrajectoryCanvasProjection = GUILayout.Toggle(
                solver.useTrajectoryCanvasProjection, " Use trajectory impact projection", toggleStyle);

            GUILayout.Label($"Impact tolerance: {solver.canvasImpactTolerance:F3} m", sliderLabelStyle);
            solver.canvasImpactTolerance = GUILayout.HorizontalSlider(solver.canvasImpactTolerance, 0.005f, 0.25f);

            GUILayout.Label($"Backtrack multiplier: {solver.impactBacktrackMultiplier:F2}", sliderLabelStyle);
            solver.impactBacktrackMultiplier = GUILayout.HorizontalSlider(solver.impactBacktrackMultiplier, 0.5f, 3f);

            GUILayout.Space(6);
            DrawSectionHeader("CANVAS TILT & DRIPS");
            solver.enableCanvasTiltControls = GUILayout.Toggle(
                solver.enableCanvasTiltControls, " Enable canvas tilt controls", toggleStyle);

            if (solver.enableCanvasTiltControls)
            {
                GUILayout.Label($"Tilt X: {solver.canvasTiltXDeg:F1}°", sliderLabelStyle);
                solver.canvasTiltXDeg = GUILayout.HorizontalSlider(solver.canvasTiltXDeg, -75f, 75f);

                GUILayout.Label($"Tilt Z: {solver.canvasTiltZDeg:F1}°", sliderLabelStyle);
                solver.canvasTiltZDeg = GUILayout.HorizontalSlider(solver.canvasTiltZDeg, -75f, 75f);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Flat", buttonStyle))
                {
                    solver.canvasTiltXDeg = 0f;
                    solver.canvasTiltZDeg = 0f;
                }
                if (GUILayout.Button("Slight Tilt", buttonStyle))
                {
                    solver.canvasTiltXDeg = 18f;
                    solver.canvasTiltZDeg = 0f;
                }
                if (GUILayout.Button("Side Tilt", buttonStyle))
                {
                    solver.canvasTiltXDeg = 0f;
                    solver.canvasTiltZDeg = 18f;
                }
                GUILayout.EndHorizontal();
            }

            solver.enablePaintDripping = GUILayout.Toggle(
                solver.enablePaintDripping, " Enable paint dripping on tilted canvas", toggleStyle);

            if (solver.enablePaintDripping)
            {
                solver.invertCanvasDripV = GUILayout.Toggle(
                    solver.invertCanvasDripV, " Invert drip V direction (fix upward dripping)", toggleStyle);


                GUILayout.Label($"Drip strength: {solver.dripStrength:F2}", sliderLabelStyle);
                solver.dripStrength = GUILayout.HorizontalSlider(solver.dripStrength, 0f, 2f);

                GUILayout.Label($"Drip every N frames: {solver.dripEveryNFrames}", sliderLabelStyle);
                solver.dripEveryNFrames = Mathf.RoundToInt(GUILayout.HorizontalSlider(solver.dripEveryNFrames, 1, 30));

                GUILayout.Label($"Max drip pixels/step: {solver.maxDripPixelsPerStep}", sliderLabelStyle);
                solver.maxDripPixelsPerStep = Mathf.RoundToInt(GUILayout.HorizontalSlider(solver.maxDripPixelsPerStep, 1, 10));

                GUILayout.Label($"Drip threshold: {solver.dripThreshold:F2}", sliderLabelStyle);
                solver.dripThreshold = GUILayout.HorizontalSlider(solver.dripThreshold, 0.01f, 0.35f);

                GUILayout.Label($"Drip drying: {solver.dripDrying:F2}", sliderLabelStyle);
                solver.dripDrying = GUILayout.HorizontalSlider(solver.dripDrying, 0f, 1f);

                GUILayout.Label($"Drip color mixing boost: {solver.dripMixBoost:F2}", sliderLabelStyle);
                solver.dripMixBoost = GUILayout.HorizontalSlider(solver.dripMixBoost, 0f, 2f);
            }
        }
        else
        {
            GUILayout.Label("(No canvas Transform assigned on the Solver)", labelStyle);
        }
        GUILayout.EndVertical();
    }

    void DrawReportSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("EXPERIMENT REPORT");
        if (solver == null) { GUILayout.EndVertical(); return; }

        GUILayout.Label($"Motion time: {solver.MotionElapsed:F2} s", labelStyle);
        GUILayout.Label($"Paths drawn (splats): {solver.PaintedSplats}", labelStyle);
        GUILayout.Label($"Particles inside bucket: {solver.InsideCount()} / {solver.maxParticles}", labelStyle);

        float pct;
        solver.CoverageArea(out pct);
        GUILayout.Label($"Canvas coverage: {pct:F1} %", labelStyle);

        if (pendulum != null)
            GUILayout.Label($"Swings: {pendulum.SwingCount} / {pendulum.n_swings}", labelStyle);

        GUILayout.Space(8);
        if (GUILayout.Button("💾  Save Canvas Image", buttonStyle))
            solver.SaveExperiment(); 
        GUILayout.EndVertical();
    }

    void DrawSurfaceTypeSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("CANVAS SURFACE");
        if (solver == null) { GUILayout.EndVertical(); return; }

        GUILayout.Label("Current: " + solver.surfaceType, labelStyle);
        GUILayout.BeginHorizontal();
        foreach (PBFSolver.SurfaceType s in System.Enum.GetValues(typeof(PBFSolver.SurfaceType)))
        {
            bool isActive = solver.surfaceType == s;
            if (GUILayout.Button(s.ToString(), isActive ? activeButtonStyle : buttonStyle))
                solver.surfaceType = s; 
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    void DrawBrushSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("BRUSH & SPLASH  (live)");
        if (solver == null) { GUILayout.EndVertical(); return; }

        GUILayout.Label($"Base brush size: {solver.baseBrushSize}", sliderLabelStyle);
        solver.baseBrushSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(solver.baseBrushSize, 1, 20));

        solver.sprayEnabled = GUILayout.Toggle(solver.sprayEnabled, " Enable spray droplets", toggleStyle);
        if (solver.sprayEnabled)
        {
            GUILayout.Label($"Droplets per splash: {solver.splashDroplets}", sliderLabelStyle);
            solver.splashDroplets = Mathf.RoundToInt(GUILayout.HorizontalSlider(solver.splashDroplets, 0, 20));
        }
        GUILayout.EndVertical();
    }

    void DrawMixingSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("CANVAS MIXING  (live)");
        if (solver == null) { GUILayout.EndVertical(); return; }

        solver.enableLightCanvasMixing = GUILayout.Toggle(
            solver.enableLightCanvasMixing, " Enable color mixing", toggleStyle);

        GUILayout.Label($"Mix strength: {solver.canvasMixStrength:F2}", sliderLabelStyle);
        solver.canvasMixStrength = GUILayout.HorizontalSlider(solver.canvasMixStrength, 0f, 1f);

        GUILayout.Label($"Paint deposit strength: {solver.paintDepositStrength:F2}", sliderLabelStyle);
        solver.paintDepositStrength = GUILayout.HorizontalSlider(solver.paintDepositStrength, 0f, 1f);
        GUILayout.EndVertical();
    }

    
    void DrawCohesionSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("FLUID COHESION  (live)");
        if (solver == null) { GUILayout.EndVertical(); return; }

        solver.enableParticleCohesion = GUILayout.Toggle(
            solver.enableParticleCohesion, " Enable cohesion", toggleStyle);

        GUILayout.Label($"Cohesion strength: {solver.particleCohesionStrength:F2}", sliderLabelStyle);
        solver.particleCohesionStrength = GUILayout.HorizontalSlider(solver.particleCohesionStrength, 0f, 8f);

        GUILayout.Label($"Cohesion radius: {solver.particleCohesionRadius:F2}", sliderLabelStyle);
        solver.particleCohesionRadius = GUILayout.HorizontalSlider(solver.particleCohesionRadius, 0.35f, 1.3f);

        GUILayout.Label($"Viscosity: {solver.viscosity:F2}", sliderLabelStyle);
        solver.viscosity = GUILayout.HorizontalSlider(solver.viscosity, 0f, 1f);
        GUILayout.EndVertical();
    }

    void DrawSloshingSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("BUCKET SLOSHING  (live)");
        if (solver == null) { GUILayout.EndVertical(); return; }

        solver.enableParticleSloshing = GUILayout.Toggle(
            solver.enableParticleSloshing, " Enable sloshing", toggleStyle);

        GUILayout.Label($"Slosh strength: {solver.particleSloshStrength:F2}", sliderLabelStyle);
        solver.particleSloshStrength = GUILayout.HorizontalSlider(solver.particleSloshStrength, 0f, 1.5f);

        GUILayout.Label($"Slosh response: {solver.particleSloshResponse:F1}", sliderLabelStyle);
        solver.particleSloshResponse = GUILayout.HorizontalSlider(solver.particleSloshResponse, 1f, 18f);
        GUILayout.EndVertical();
    }

    void DrawParticleCountSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("PARTICLE COUNT  (needs restart)");
        if (solver == null) { GUILayout.EndVertical(); return; }

        GUILayout.Label($"Count: {solver.maxParticles:N0}", sliderLabelStyle);
        solver.maxParticles = Mathf.RoundToInt(
            GUILayout.HorizontalSlider(solver.maxParticles, 1000, 200000));

        GUILayout.Space(4);
        if (GUILayout.Button("↻  Restart With New Count", buttonStyle))
        {
            solver.RestartSimulation();
        }
        GUILayout.EndVertical();
    }

    static readonly Color PresetRed = new Color(0.85f, 0.15f, 0.15f, 1f);
    static readonly Color PresetYellow = new Color(0.90f, 0.80f, 0.10f, 1f);
    static readonly Color PresetBlue = new Color(0.15f, 0.35f, 0.85f, 1f);

    void DrawColorsSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("LAYER COLORS  (live)");
        if (solver == null || solver.layerPaintColors == null) { GUILayout.EndVertical(); return; }

        string[] names = { "Red", "Blue", "Yellow", "Green" };

        for (int i = 0; i < solver.layerPaintColors.Length; i++)
        {
            string label = i < names.Length ? names[i] : $"Layer {i + 1}";
            GUILayout.Label(label, labelStyle);
            Color c = solver.layerPaintColors[i];

            GUILayout.BeginHorizontal();
            DrawColorSwatch(c);
            GUILayout.BeginVertical();
            c.r = SliderRow("R", c.r);
            c.g = SliderRow("G", c.g);
            c.b = SliderRow("B", c.b);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (DrawPresetSwatchButton(PresetRed)) c = PresetRed;
            if (DrawPresetSwatchButton(PresetYellow)) c = PresetYellow;
            if (DrawPresetSwatchButton(PresetBlue)) c = PresetBlue;
            GUILayout.EndHorizontal();

            solver.layerPaintColors[i] = c;
            GUILayout.Space(6);
        }
        GUILayout.EndVertical();
    }

    bool DrawPresetSwatchButton(Color c)
    {
        Color old = GUI.color;
        bool pressed;
        try
        {
            GUI.color = c;
            pressed = GUILayout.Button(whiteTex, GUILayout.Width(48), GUILayout.Height(24));
        }
        finally
        {
            GUI.color = old;
        }
        return pressed;
    }

    float SliderRow(string channel, float value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(channel, sliderLabelStyle, GUILayout.Width(14));
        float v = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.Width(220));
        GUILayout.EndHorizontal();
        return v;
    }

    void DrawColorSwatch(Color c)
    {
        Color old = GUI.color;
        try
        {
            GUI.color = c;
            GUILayout.Label(whiteTex, GUILayout.Width(28), GUILayout.Height(60));
        }
        finally
        {
            GUI.color = old;
        }
    }

    
    Texture2D compareTexOld, compareTexNew;
    string compareNameOld = "", compareNameNew = "";
    bool compareLoadedOnce = false;

    string GetSaveDirectory()
    {
        if (solver != null && !string.IsNullOrEmpty(solver.saveFolder))
            return solver.saveFolder;
        return System.IO.Path.Combine(Application.persistentDataPath, "PaintResults");
    }

    Texture2D LoadPng(string path)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        Texture2D tex = new Texture2D(2, 2);
        tex.LoadImage(bytes); 
        return tex;
    }

    void LoadLastTwoExperiments()
    {
        compareLoadedOnce = true;

        if (compareTexOld != null) Destroy(compareTexOld);
        if (compareTexNew != null) Destroy(compareTexNew);
        compareTexOld = compareTexNew = null;
        compareNameOld = compareNameNew = "";

        string dir = GetSaveDirectory();
        if (!System.IO.Directory.Exists(dir)) return;

        string[] files = System.IO.Directory.GetFiles(dir, "canvas_*.png");
     
        System.Array.Sort(files);

        if (files.Length >= 1)
        {
            string newest = files[files.Length - 1];
            compareTexNew = LoadPng(newest);
            compareNameNew = System.IO.Path.GetFileName(newest);
        }
        if (files.Length >= 2)
        {
            string previous = files[files.Length - 2];
            compareTexOld = LoadPng(previous);
            compareNameOld = System.IO.Path.GetFileName(previous);
        }
    }

    void DrawCompareSection()
    {
        GUILayout.BeginVertical(sectionBoxStyle);
        DrawSectionHeader("COMPARE EXPERIMENTS");

        if (!compareLoadedOnce)
            LoadLastTwoExperiments();

        if (GUILayout.Button("🔄  Reload Last Two Saved", buttonStyle))
            LoadLastTwoExperiments();

        GUILayout.Space(6);

        if (compareTexNew == null)
        {
            GUILayout.Label("No saved experiments found.", labelStyle);
            GUILayout.Label("Save from the Report tab first,", sliderLabelStyle);
            GUILayout.Label("then press Reload.", sliderLabelStyle);
            GUILayout.EndVertical();
            return;
        }

        GUILayout.BeginHorizontal();
        DrawCompareColumn("Previous", compareNameOld, compareTexOld);
        GUILayout.Space(8);
        DrawCompareColumn("Latest", compareNameNew, compareTexNew);
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.Label("Folder: " + GetSaveDirectory(), sliderLabelStyle);
        GUILayout.EndVertical();
    }

    void DrawCompareColumn(string title, string fileName, Texture2D tex)
    {
        GUILayout.BeginVertical(GUILayout.Width(185));
        GUILayout.Label(title, labelStyle);

        if (tex != null)
        {
            Rect r = GUILayoutUtility.GetRect(180, 180,
                GUILayout.Width(180), GUILayout.Height(180));
            GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
            GUILayout.Label(fileName, sliderLabelStyle);
        }
        else
        {
            GUILayout.Label("(only one experiment saved)", sliderLabelStyle);
        }
        GUILayout.EndVertical();
    }
}