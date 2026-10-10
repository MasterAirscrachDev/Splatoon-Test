using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Top-right gauges for the local player: the small circle is the ink tank with a mark at the
// sub's cost, the big one the special's charge (draining while it runs). Both are liquid fills
// (UI/WavyFill) in the team colour.
public class LoadoutHUD : MonoBehaviour
{
    [SerializeField] GameObject root;             // hidden without a local player on a team
    [SerializeField] Image subFill, specialFill;
    [SerializeField] RectTransform subCostMark;   // line across the sub gauge at the cost
    [SerializeField] RectTransform specialFrame;  // pulses when the special is ready
    [SerializeField] GameObject specialReady;     // "READY" label
    [SerializeField] TMP_Text subLabel;           // dimmed with the gauge without enough ink
    [SerializeField] TMP_Text specialLabel;
    [SerializeField] float fillSmoothing = 10f;
    [SerializeField] float lowInkDim = 0.45f;     // sub gauge brightness without enough ink

    static readonly int FillId = Shader.PropertyToID("_Fill");
    static readonly int PhaseId = Shader.PropertyToID("_Phase");

    Material subMaterial, specialMaterial;
    float shownSub, shownSpecial;

    public float SubFill => shownSub;
    public float SpecialFill => shownSpecial;
    public bool ReadyShown => specialReady.activeSelf;
    public string SubName => subLabel.text;
    public string SpecialName => specialLabel != null ? specialLabel.text : "";

    void Awake()
    {
        subMaterial = subFill.material = new Material(subFill.material);
        specialMaterial = specialFill.material = new Material(specialFill.material);
        specialMaterial.SetFloat(PhaseId, 0.37f); // don't wave in step with the sub gauge
    }

    void OnDestroy()
    {
        if (subMaterial != null) Destroy(subMaterial);
        if (specialMaterial != null) Destroy(specialMaterial);
    }

    void Update()
    {
        PlayerLoadout loadout = PlayerLoadout.Local;
        NetGameManager gm = NetGameManager.Instance;
        bool show = loadout != null && gm != null && loadout.Player.Team != 0 && loadout.Player.isActiveAndEnabled;
        if (root.activeSelf != show) root.SetActive(show);
        if (!show) return;

        PlayerController p = loadout.Player;
        Color team = gm.TeamColour(p.Team);
        float k = 1f - Mathf.Exp(-fillSmoothing * Time.deltaTime);

        // Sub: ink level, dimmed below the cost.
        shownSub = Mathf.Lerp(shownSub, p.InkLevel, k);
        subMaterial.SetFloat(FillId, shownSub);
        bool canSub = loadout.CanUseSub;
        subFill.color = canSub ? team : new Color(team.r * lowInkDim, team.g * lowInkDim, team.b * lowInkDim, 1f);
        float h = ((RectTransform)subFill.transform).rect.height;
        subCostMark.anchoredPosition = new Vector2(0f, (loadout.SubInkCost - 0.5f) * h);
        subLabel.alpha = canSub ? 1f : 0.5f;
        if (subLabel.text != loadout.SubName) subLabel.text = loadout.SubName; // stand in for the sub and special icons
        if (specialLabel != null && specialLabel.text != loadout.SpecialName) specialLabel.text = loadout.SpecialName;

        // Special: charge, or the time left while it's running.
        float target = loadout.ShieldActive ? loadout.ShieldRemaining / loadout.ShieldDuration : loadout.SpecialCharge;
        shownSpecial = loadout.ShieldActive ? target : Mathf.Lerp(shownSpecial, target, k);
        specialMaterial.SetFloat(FillId, shownSpecial);
        specialFill.color = team;
        bool ready = loadout.SpecialReady;
        if (specialReady.activeSelf != ready) specialReady.SetActive(ready);
        specialFrame.localScale = Vector3.one * (ready ? 1f + 0.06f * Mathf.Sin(Time.time * 7f) : 1f);
    }
}
