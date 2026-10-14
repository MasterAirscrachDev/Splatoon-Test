using UnityEngine;

// Sniper (a charger): hold attack to charge (a laser shows where it'll go; a light on the gun's front
// comes on once it's full), let go to fire. It aims at what the cursor's on (or the point down the
// cursor's line a full charge reaches), but can't tip further up or down than aimPitchLimits. It only
// gets as far as this charge reaches, so short of the cursor the laser (and the true reticle with it)
// ends partway along that line, closing on the cursor as it charges. The shot itself
// is unseen: it flies dead straight, near instantly, farther and harder the more it was charged (a
// full charge is an instakill on a direct hit), and paints where it ends on a surface. What shows is
// the ink it leaves all along its line: blobs that fall straight down from it (and paint where they
// land), bigger (and drawn bigger) the more it was charged. Other players see the laser while we charge, and the line
// replayed.
public class Sniper : ChargeWeapon
{
    [Header("Shot")]
    [SerializeField, Tooltip("How far the shot flies with no charge and with a full charge (metres)")] Vector2 range = new Vector2(10f, 26f);
    [SerializeField, Tooltip("Direct-hit damage with no charge and with a full charge (100 splats: a full charge is an instakill)")] Vector2 damage = new Vector2(40f, 160f);
    [SerializeField, Tooltip("How fast the (unseen) shot flies (metres per second): near instant")] float shotSpeed = 120f;
    [SerializeField, Tooltip("Its splat where it ends on a surface")] int shotSplat = 12;
    [SerializeField, Tooltip("How far down and up it can aim (degrees from level). Aiming past these, it can't reach the cursor")] Vector2 aimPitchLimits = new Vector2(-50f, 60f);
    [SerializeField, Tooltip("Ink per full-charge shot, in thousandths of a full tank (less the less it's charged, down to Min Ink Share)")] float inkCost = 180f;
    [SerializeField, Range(0f, 1f), Tooltip("The share of the ink cost a shot with no charge still costs")] float minInkShare = 0.3f;

    [Header("Ink along the line")]
    [SerializeField, Tooltip("A blob of ink falls from the shot's line every this many metres")] float lineSpacing = 0.6f;
    [SerializeField, Tooltip("Each blob's splat size with no charge and with a full charge (also how big it's drawn)")] Vector2 lineSplat = new Vector2(5f, 10f);
    [SerializeField, Tooltip("How fast the blobs start falling (metres per second)")] float lineFallSpeed = 4f;
    [SerializeField, Tooltip("How the falling blobs are drawn")] ShotLook lineLook = new ShotLook { size = 0.8f, companions = 1 };
    [SerializeField, Tooltip("Their drawn size, x Line Look's, with no charge and with a full charge")] Vector2 lineLookScale = new Vector2(0.6f, 1.4f);

    [Header("Laser")]
    [SerializeField, Tooltip("The aiming laser's width (metres)")] float laserWidth = 0.04f;
    [SerializeField, Range(0f, 1f), Tooltip("How see-through the laser is")] float laserAlpha = 0.8f;
    [SerializeField, Range(0f, 1f), Tooltip("How far the laser's colour is greyed from the ink colour")] float laserGrey = 0.45f;
    [SerializeField, Range(0f, 1f), Tooltip("How bright the laser's colour is, x the ink colour")] float laserShade = 0.7f;

    [Header("Charged light")]
    [SerializeField, Tooltip("Size of the glow on the gun's front that shows a full charge (metres)")] float glowSize = 0.07f;
    [SerializeField, Tooltip("Its light's reach (metres)")] float glowRange = 1.5f;
    [SerializeField, Tooltip("Its light's brightness")] float glowIntensity = 2f;

    static readonly Ballistics Straight = new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) }; // no drop, no slowing
    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int AlphaId = Shader.PropertyToID("_Alpha");
    static readonly int PaleId = Shader.PropertyToID("_Pale");
    static readonly int EdgeDarkId = Shader.PropertyToID("_EdgeDark");

    LineRenderer laser;
    MaterialPropertyBlock laserBlock, glowBlock;
    MeshRenderer glow;
    Light glowLight;
    CameraAim camAim;
    float remoteDownFor; // other players' copies: how long they've been charging (their WeaponDown)

    public Vector2 Range => range;
    public Vector2 Damage => damage;
    public float LineSpacing => lineSpacing;
    public int Shots { get; private set; }
    public float LastCharge { get; private set; }     // the last shot's (tests)
    public float LastDamage { get; private set; }
    public float LastLineLength { get; private set; } // how far along the line ink falls
    public int LastLineSplat { get; private set; }
    public bool LaserShown => laser != null && laser.enabled;
    public float LaserWidth => laser != null ? laser.widthMultiplier : 0f;
    public bool GlowShown => glow != null && glow.enabled;
    public float RangeAt(float charge) => Mathf.Lerp(range.x, range.y, charge);
    public float DamageAt(float charge) => Mathf.Lerp(damage.x, damage.y, charge);
    public int LineSplatAt(float charge) => Mathf.RoundToInt(Mathf.Lerp(lineSplat.x, lineSplat.y, charge));
    public float LineLookSizeAt(float charge) => lineLook.size * Mathf.Lerp(lineLookScale.x, lineLookScale.y, charge);
    public float LastLineLookSize { get; private set; }
    public Vector3? ScriptedAim { get; set; } // tests: aim this way (world) instead of down the cursor's line

    protected override void Start()
    {
        base.Start();
        laser = MakeLaser();
        MakeGlow();
        if (Owner != null) camAim = Owner.GetComponentInChildren<CameraAim>(true);
    }

    protected override void Release(PlayerController player, float charge)
    {
        if (!player.ConsumeInk(inkCost * Mathf.Lerp(minInkShare, 1f, charge) * 0.001f)) return;
        Shots++;
        float reach = RangeAt(charge);
        AimLine(reach, true, out Vector3 dir, out float length);
        Vector3 from = muzzle.position;
        LastCharge = charge;
        LastDamage = DamageAt(charge);
        LastLineSplat = LineSplatAt(charge);
        LastLineLength = length;
        LastLineLookSize = LineLookSizeAt(charge);
        FeetInkNow(player);
        Fire(from, dir, reach, length, LastDamage, LastLineSplat, charge, player.Team, player.OwnerId, authoritative: true);
        Vector3 launch = dir * shotSpeed;
        // (Its line's length and reach ride along as extra "sizes", in centimetres, and its blobs' size:
        // the replay drops the same line.)
        NetGameManager.Instance?.BroadcastShots(player, from, Vector3.zero, new[] { launch.x, launch.y, launch.z },
                                                new[] { shotSplat, Mathf.RoundToInt(length * 100f), Mathf.RoundToInt(reach * 100f), LastLineSplat },
                                                new[] { false, false, false, false });
    }

    // Where we aim for a shot reaching `reach`: at what the cursor's on, or the point down its line a
    // full charge reaches, within the pitch limits; `turn`: the weapon turned to it. Out: the way to
    // fire, and how far until it ends (a surface, the aim point, or this shot's reach: a shorter charge
    // stops short of the cursor).
    void AimLine(float reach, bool turn, out Vector3 dir, out float length)
    {
        Vector3 from = muzzle.position;
        float full = range.y;
        Vector3 target = from + transform.forward * full;
        if (ScriptedAim.HasValue) target = from + ScriptedAim.Value.normalized * full;
        else if (camAim != null)
        {
            Transform eye = camAim.transform;
            Vector3 o = eye.position, d = Quaternion.AngleAxis(-PlayerController.AimRaise, eye.right) * eye.forward;
            // The point on the cursor's line a full charge reaches from the muzzle: |o + d t - from| = full.
            Vector3 m = o - from;
            float b = Vector3.Dot(d, m), c = m.sqrMagnitude - full * full, disc = b * b - c;
            float t = disc >= 0f ? -b + Mathf.Sqrt(disc) : full;
            if (Physics.Raycast(o, d, out RaycastHit seen, t, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) t = seen.distance;
            target = o + d * Mathf.Max(t, 0.5f);
        }
        dir = (target - from).normalized;
        // Within the pitch limits.
        float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        float limited = Mathf.Clamp(pitch, aimPitchLimits.x, aimPitchLimits.y);
        bool clamped = !Mathf.Approximately(pitch, limited);
        if (clamped)
        {
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : transform.forward;
            dir = Quaternion.AngleAxis(-limited, Vector3.Cross(Vector3.up, flat)) * flat;
        }
        if (turn) transform.rotation = Quaternion.LookRotation(dir);
        float most = clamped ? reach : Mathf.Min(reach, Vector3.Distance(from, target) + 0.05f);
        length = LineLength(muzzle.position, dir, most);
    }

    // Where the shot would end now (ours): the true reticle shows it.
    public override bool AimEnd(out Vector3 point)
    {
        point = default;
        if (Owner == null || !Owner.IsLocalPlayer || muzzle == null) return false;
        AimLine(RangeAt(Charge), false, out Vector3 dir, out float length);
        point = muzzle.position + dir * length;
        return true;
    }

    // The (unseen) shot, and a blob falling from every lineSpacing along its line, drawn bigger the
    // more it was charged.
    void Fire(Vector3 from, Vector3 dir, float reach, float length, float hit, int blobSplat, float charge, int team, ulong ownerId, bool authoritative)
    {
        ProjectileManager.Fire(from, dir * shotSpeed, shotSplat, team, false, authoritative, ownerId, hit,
                               ballistics: Straight, source: Source, maxDistance: reach);
        ShotLook look = lineLook.Scaled(Mathf.Lerp(lineLookScale.x, lineLookScale.y, charge));
        for (float d = lineSpacing * 0.5f; d < length; d += lineSpacing)
            ProjectileManager.Fire(from + dir * d, Vector3.down * lineFallSpeed, blobSplat, team, true, authoritative, ownerId, 0f,
                                   source: Source, look: look);
    }

    // How far along its line the shot's ink goes: to the first surface, or its full reach.
    static float LineLength(Vector3 from, Vector3 dir, float reach) =>
        Physics.Raycast(from, dir, out RaycastHit hit, reach, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore) ? hit.distance : reach;

    // Their shot and its line, visual-only here.
    public override void Replay(ProjectileSpawnData d)
    {
        if (d.velocities == null || d.velocities.Length < 3 || d.splashSizes == null || d.splashSizes.Length < 3) return;
        Vector3 launch = new Vector3(d.velocities[0], d.velocities[1], d.velocities[2]);
        if (launch.sqrMagnitude < 1e-6f) return;
        int blobs = d.splashSizes.Length > 3 ? d.splashSizes[3] : LineSplatAt(1f);
        float charge = Mathf.InverseLerp(lineSplat.x, lineSplat.y, blobs); // (its blobs' size says how charged it was)
        Fire(d.origin, launch.normalized, d.splashSizes[2] / 100f, d.splashSizes[1] / 100f, 0f, blobs, charge, d.team, d.shooterSteamId, authoritative: false);
    }

    // The aiming laser: while we charge (or hold a charge in swim form, from where we'd surface), and
    // on other players' copies while they're charging (their WeaponDown). The glow on the gun's front
    // once the charge is full (theirs: once they've been charging a full charge's time).
    void LateUpdate()
    {
        PlayerController player = Owner;
        if (player == null || laser == null) return;
        bool local = player.IsLocalPlayer;
        bool show = local ? Charging : player.WeaponDown && !player.IsSquid;
        remoteDownFor = !local && show ? remoteDownFor + Time.deltaTime : 0f;
        bool full = show && (local ? Charge >= 1f : remoteDownFor >= ChargeTime);
        laser.enabled = show;
        SetGlow(full, player.TeamColour);
        if (!show) return;
        float length;
        Vector3 dir;
        if (local) AimLine(RangeAt(Charge), true, out dir, out length); // (turns the weapon to it)
        else { dir = muzzle.forward; length = LineLength(muzzle.position, dir, RangeAt(1f)); }
        Vector3 from = muzzle.position;
        laser.SetPosition(0, from);
        laser.SetPosition(1, from + dir * length);
        laser.widthMultiplier = laserWidth;
        // One flat, dull shade of the ink colour.
        Color ink = player.TeamColour;
        Color dull = Color.Lerp(ink, Color.white * ink.grayscale, laserGrey) * laserShade;
        laserBlock.SetColor(ColourId, dull);
        laserBlock.SetFloat(AlphaId, laserAlpha);
        laserBlock.SetFloat(PaleId, 0f);
        laserBlock.SetFloat(EdgeDarkId, 1f);
        laser.SetPropertyBlock(laserBlock);
    }

    LineRenderer MakeLaser()
    {
        var go = new GameObject("Laser");
        go.transform.SetParent(transform, false);
        LineRenderer l = go.AddComponent<LineRenderer>();
        l.useWorldSpace = true;
        l.positionCount = 2;
        l.alignment = LineAlignment.View;
        l.textureMode = LineTextureMode.Stretch; // (v across it: the line shader's antialiased sides)
        l.numCapVertices = 2;
        l.sharedMaterial = Resources.Load<Material>("SubPreviewLine");
        l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        l.receiveShadows = false;
        l.enabled = false;
        laserBlock = new MaterialPropertyBlock();
        return l;
    }

    // A small bright bead on the muzzle, with a little light of its own: shown at full charge.
    void MakeGlow()
    {
        if (muzzle == null) return;
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "ChargedGlow";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(muzzle, false);
        go.transform.localScale = Vector3.one * glowSize;
        glow = go.GetComponent<MeshRenderer>();
        glow.sharedMaterial = Resources.Load<Material>("SubPreviewLine");
        glow.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        glow.receiveShadows = false;
        glow.enabled = false;
        glowBlock = new MaterialPropertyBlock();
        glowLight = go.AddComponent<Light>();
        glowLight.type = LightType.Point;
        glowLight.range = glowRange;
        glowLight.intensity = glowIntensity;
        glowLight.shadows = LightShadows.None;
        glowLight.enabled = false;
    }

    void SetGlow(bool on, Color colour)
    {
        if (glow == null) return;
        glow.enabled = on;
        glowLight.enabled = on;
        if (!on) return;
        glowBlock.SetColor(ColourId, Color.Lerp(colour, Color.white, 0.35f)); // bright: pale toward white
        glowBlock.SetFloat(AlphaId, 1f);
        glowBlock.SetFloat(PaleId, 0f);
        glowBlock.SetFloat(EdgeDarkId, 1f);
        glow.SetPropertyBlock(glowBlock);
        glowLight.color = colour;
    }
}
