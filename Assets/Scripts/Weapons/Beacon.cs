using UnityEngine;

// Sub weapon: a Super Jump point for its team (see SubDevice for health and lifetime). Landing on
// it breaks it.
public class Beacon : SubDevice, ISuperJumpTarget
{
    [SerializeField] Transform spinner; // turns while active

    public override SubType Type => SubType.Beacon;

    protected override void Update()
    {
        if (spinner != null) spinner.Rotate(0f, 120f * Time.deltaTime, 0f, Space.World);
        base.Update();
    }

    public Vector3 JumpPosition => transform.position;
    public bool IsValidTargetFor(PlayerController jumper) => !broken && jumper != null && jumper.Team == Team;
    public void OnSuperJumpLanded(PlayerController jumper) => Break();
}
