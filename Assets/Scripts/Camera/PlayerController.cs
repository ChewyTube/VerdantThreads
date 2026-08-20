using UnityEngine;

// 玩家角色控制器：MC 同款体素碰撞（方案 C，见 docs/design/VOXEL_COLLISION.md）。
// transform.position = 眼睛位置；身体 AABB 中心 = (x, y - EYE_HEIGHT + HALF_HEIGHT, z)。
// 行走模式：重力 + 跳跃 + 自动上台阶 + 体素碰撞。
// Ctrl+F 飞行模式：无重力、可上下飞，仍有方块碰撞（不能穿墙）。
// Ctrl+D 调试模式：无重力 + 穿墙（noclip，直接移动 transform，原型测试地形用）。
// 背包窗打开时暂停输入（鼠标解锁用于点击 IMGUI）。
// 由 World.Start 挂到相机上（替换原 CameraMove 自由飞行）。
public class PlayerController : MonoBehaviour
{
    [Header("视角设置")]
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float minPitch = -89f;
    [SerializeField] private float maxPitch = 89f;

    private float pitch;
    private Vector3 velocity; // 身体速度（blocks/s）
    private bool flying;      // Ctrl+F 飞行模式（无重力，仍有碰撞）
    private bool debugMode;   // Ctrl+D 调试模式（无重力 + 穿墙）
    private bool onGround;    // 上一帧是否站在地面（决定能否跳跃）

    private World world; // 缓存引用：背包窗打开时暂停输入；体素碰撞查询
    private System.Func<Vector3Int, bool> isSolid; // 缓存委托，避免每帧分配

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        pitch = transform.eulerAngles.x;
        if (pitch > 180f) pitch -= 360f;

        if (world == null) world = FindObjectOfType<World>();
        isSolid = pos => world.IsSolid(pos.x, pos.y, pos.z);
    }

    void Update()
    {
        if (world != null && world.Backpack != null && world.Backpack.BackpackOpen) return;

        HandleRotation();

        // Ctrl+F 切换飞行模式（无重力，仍有方块碰撞）；切换时清零速度，避免残留动量
        if (IsCtrlHeld() && Input.GetKeyDown(KeyCode.F))
        {
            flying = !flying;
            velocity = Vector3.zero;
            Debug.Log(flying ? "飞行模式：开启（Ctrl+F 关闭）" : "飞行模式：关闭");
        }

        // Ctrl+D 切换调试模式（无重力 + 穿墙）；切换时清零速度
        if (IsCtrlHeld() && Input.GetKeyDown(KeyCode.D))
        {
            debugMode = !debugMode;
            velocity = Vector3.zero;
            Debug.Log(debugMode ? "调试模式：开启（穿墙，Ctrl+D 关闭）" : "调试模式：关闭");
        }

        if (debugMode) HandleDebugMovement();
        else if (flying) HandleFlyMovement();
        else HandleWalkMovement();
    }

    // Ctrl 是否按住（左右均可）
    private static bool IsCtrlHeld()
    {
        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }

    private void HandleRotation()
    {
        float yaw = Input.GetAxis("Mouse X") * mouseSensitivity;
        transform.Rotate(Vector3.up, yaw, Space.World);

        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        transform.localEulerAngles = new Vector3(pitch, transform.eulerAngles.y, 0f);
    }

    // 飞行模式（Ctrl+F）：无重力、可上下飞，仍有方块碰撞（不能穿墙）。
    // 速度每帧由输入直接设定（无惯性），碰撞由 VoxelCollision 逐轴解析。
    private void HandleFlyMovement()
    {
        float verticalInput = 0f;
        if (Input.GetKey(KeyCode.Space)) verticalInput += 1f;
        if (Input.GetKey(KeyCode.LeftShift)) verticalInput -= 1f;

        Vector3 inputDir = new Vector3(
            Input.GetAxisRaw("Horizontal"), verticalInput, Input.GetAxisRaw("Vertical")).normalized;

        Vector3 moveDir = RemoveY(transform.forward).normalized * inputDir.z
                        + RemoveY(transform.right).normalized * inputDir.x
                        + Vector3.up * inputDir.y;

        // 身体 AABB 中心（眼睛位置下方）
        Vector3 bodyCenter = transform.position - Vector3.up * (Constants.PLAYER_EYE_HEIGHT - Constants.PLAYER_HALF_HEIGHT);
        Vector3 halfExtents = new Vector3(Constants.PLAYER_HALF_WIDTH, Constants.PLAYER_HALF_HEIGHT, Constants.PLAYER_HALF_WIDTH);

        // 逐轴移动 + 碰撞（无重力；碰撞轴速度归零，下一帧由输入重新设定）
        Vector3 vel = moveDir * (Constants.PLAYER_WALK_SPEED * 2.5f);
        VoxelCollision.Move(ref bodyCenter, halfExtents, ref vel, Time.deltaTime, isSolid);

        transform.position = bodyCenter + Vector3.up * (Constants.PLAYER_EYE_HEIGHT - Constants.PLAYER_HALF_HEIGHT);
    }

    // 调试模式（Ctrl+D）：无重力 + 穿墙（noclip，直接移动 transform，原型测试地形用）
    private void HandleDebugMovement()
    {
        float verticalInput = 0f;
        if (Input.GetKey(KeyCode.Space)) verticalInput += 1f;
        if (Input.GetKey(KeyCode.LeftShift)) verticalInput -= 1f;

        Vector3 inputDir = new Vector3(
            Input.GetAxisRaw("Horizontal"), verticalInput, Input.GetAxisRaw("Vertical")).normalized;

        Vector3 moveDir = RemoveY(transform.forward).normalized * inputDir.z
                        + RemoveY(transform.right).normalized * inputDir.x
                        + Vector3.up * inputDir.y;
        transform.position += moveDir * (Constants.PLAYER_WALK_SPEED * 2.5f) * Time.deltaTime;
    }

    // 行走模式：重力 + 跳跃 + 台阶 + 体素碰撞
    private void HandleWalkMovement()
    {
        Vector3 inputDir = new Vector3(
            Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical")).normalized;

        // 水平移动方向（相对相机 yaw，忽略俯仰）
        Vector3 moveDir = RemoveY(transform.forward).normalized * inputDir.z
                        + RemoveY(transform.right).normalized * inputDir.x;

        // 目标水平速度
        Vector3 targetH = moveDir * Constants.PLAYER_WALK_SPEED;

        // 跳跃（仅地面）
        if (Input.GetKeyDown(KeyCode.Space) && onGround)
            velocity.y = Constants.PLAYER_JUMP_SPEED;

        // 身体 AABB 中心（眼睛位置下方）
        Vector3 bodyCenter = transform.position - Vector3.up * (Constants.PLAYER_EYE_HEIGHT - Constants.PLAYER_HALF_HEIGHT);
        Vector3 halfExtents = new Vector3(Constants.PLAYER_HALF_WIDTH, Constants.PLAYER_HALF_HEIGHT, Constants.PLAYER_HALF_WIDTH);

        // 水平移动 + 台阶：先试水平，被挡且在地面时尝试上台阶
        Vector3 startCenter = bodyCenter;
        Vector3 hVel = targetH;
        bool blocked = TryHorizontalMove(ref bodyCenter, halfExtents, ref hVel, Time.deltaTime);

        if (blocked && onGround)
        {
            // 台阶：从起点上移 STEP → 全速水平 → 下移回地面（下方有空间则落下，否则停在台阶上）
            Vector3 stepCenter = startCenter + Vector3.up * Constants.PLAYER_STEP_HEIGHT;
            Vector3 stepVel = targetH;
            bool stepBlocked = TryHorizontalMove(ref stepCenter, halfExtents, ref stepVel, Time.deltaTime);
            if (!stepBlocked)
            {
                VoxelCollision.MoveAxis(stepCenter, halfExtents, 1, -Constants.PLAYER_STEP_HEIGHT, out float my, isSolid);
                stepCenter.y += my;
                bodyCenter = stepCenter;
            }
            // 台阶失败：保留第一次被挡后的位置（部分移动）
        }

        // 垂直移动（重力/跳跃）
        if (onGround && velocity.y <= 0f)
        {
            // 站在地面：不累积重力（高帧率下重力步长 < Epsilon 会在地面来回吸附 → 抖动）。
            // 向下探测 0.01：脚下仍有方块则保持站立；否则（走出边缘/脚下方块被挖）开始下落。
            velocity.y = 0f;
            Vector3 probeCenter = bodyCenter;
            bool groundBelow = VoxelCollision.MoveAxis(probeCenter, halfExtents, 1, -0.01f, out _, isSolid);
            if (!groundBelow) onGround = false;
        }
        else
        {
            // 空中：重力 + 垂直移动
            velocity.y -= Constants.PLAYER_GRAVITY * Time.deltaTime;
            bool falling = velocity.y < 0f;
            bool verticalBlocked = VoxelCollision.MoveAxis(bodyCenter, halfExtents, 1, velocity.y * Time.deltaTime, out float my2, isSolid);
            bodyCenter.y += my2;
            if (verticalBlocked) velocity.y = 0f;
            onGround = verticalBlocked && falling;
        }

        // 眼睛位置 = 身体中心 + (EYE_HEIGHT - HALF_HEIGHT)
        transform.position = bodyCenter + Vector3.up * (Constants.PLAYER_EYE_HEIGHT - Constants.PLAYER_HALF_HEIGHT);
    }

    // 水平移动（X、Z 逐轴），返回是否被挡
    private bool TryHorizontalMove(ref Vector3 center, Vector3 halfExtents, ref Vector3 hVel, float dt)
    {
        bool blocked = false;
        if (VoxelCollision.MoveAxis(center, halfExtents, 0, hVel.x * dt, out float mx, isSolid)) { hVel.x = 0f; blocked = true; }
        center.x += mx;
        if (VoxelCollision.MoveAxis(center, halfExtents, 2, hVel.z * dt, out float mz, isSolid)) { hVel.z = 0f; blocked = true; }
        center.z += mz;
        return blocked;
    }

    // 去掉 Y 分量（把相机朝向投影到水平面）
    private static Vector3 RemoveY(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}