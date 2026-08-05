using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Lockyaw.VoiceChat.FirstPersonSample {

    [AddComponentMenu("Lockyaw Voice Chat/First-Person Voice Player")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(CharacterController))]
    public sealed class FirstPersonVoicePlayer : NetworkBehaviour {

        [SerializeField, HideInInspector] private CharacterController characterController;
        [SerializeField, HideInInspector] private MicrophoneInput microphoneInput;

        [Header("References")]
        [SerializeField] private InputActionAsset inputActions;
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener audioListener;
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private Animator playerAnimator;
        [SerializeField] private Collider remoteCollision;
        [SerializeField] private Renderer[] localHiddenRenderers;

        [Header("Movement")]
        [SerializeField, Min(0f)] private float moveSpeed = 4.7f;
        [SerializeField, Min(0f)] private float sprintSpeed = 7f;
        [SerializeField, Min(0f)] private float speedChangeRate = 18f;
        [SerializeField, Min(0f)] private float jumpHeight = 1.2f;
        [SerializeField] private float gravity = -18f;
        [SerializeField, Min(0f)] private float terminalVelocity = 53f;
        [SerializeField] private float groundedVelocity = -3.75f;

        [Header("Look")]
        [SerializeField, Min(0f)] private float mouseLookSensitivity = .12f;
        [SerializeField, Min(0f)] private float gamepadLookSpeed = 150f;
        [SerializeField, Range(0f, 89f)] private float verticalLookLimit = 85f;

        private static readonly int SPEED_PARAMETER = Animator.StringToHash("Speed");

        private InputActionAsset runtimeInputActions;
        private InputAction moveAction;
        private InputAction lookAction;
        private InputAction jumpAction;
        private InputAction sprintAction;
        private InputAction menuAction;
        private Vector3 horizontalVelocity;
        private float verticalVelocity;
        private float cameraPitch;
        private bool hasLocalControl;
        private bool hasConfiguredLocalControl;
        private bool isCursorLocked;

        private void Awake() {
            CacheComponentReferences();
            ConfigureLocalControl(false);
        }

        public override void OnDestroy() {
            ConfigureLocalControl(false);
            base.OnDestroy();
        }

        private void OnEnable() {
            ConfigureLocalControl(IsSpawned && IsClient && IsOwner);
        }

        private void OnDisable() {
            ConfigureLocalControl(false);
        }

        private void Update() {
            if (!hasLocalControl) { return; }

            UpdateCursor();
            UpdateLook();
            UpdateMovement();
        }

        private void OnApplicationFocus(bool hasFocus) {
            if (!hasLocalControl) { return; }
            SetCursorLocked(hasFocus);
        }

        private void Reset() {
            CacheComponentReferences();
        }

        private void OnValidate() {
            CacheComponentReferences();
        }

        public override void OnNetworkSpawn() {
            ConfigureLocalControl(IsClient && IsOwner);
        }

        public override void OnNetworkDespawn() {
            ConfigureLocalControl(false);
        }

        public override void OnGainedOwnership() {
            base.OnGainedOwnership();
            ConfigureLocalControl(IsClient);
        }

        public override void OnLostOwnership() {
            ConfigureLocalControl(false);
            base.OnLostOwnership();
        }

        private void ConfigureLocalControl(bool shouldEnable) {
            bool inputStateMatches = shouldEnable ? runtimeInputActions != null : runtimeInputActions == null;
            if (hasConfiguredLocalControl && hasLocalControl == shouldEnable && inputStateMatches) { return; }

            bool hadLocalControl = hasLocalControl;
            hasLocalControl = shouldEnable;
            hasConfiguredLocalControl = true;
            characterController.enabled = shouldEnable;
            if (microphoneInput != null) {
                microphoneInput.enabled = shouldEnable;
            }
            if (remoteCollision != null) {
                remoteCollision.enabled = !shouldEnable;
            }
            if (playerCamera != null) {
                playerCamera.gameObject.SetActive(shouldEnable);
            }
            if (audioListener != null) {
                audioListener.enabled = shouldEnable;
            }

            SetRenderersEnabled(!shouldEnable);
            if (shouldEnable) {
                EnableInput();
                SetCursorLocked(true);
            } else {
                DisableInput();
                if (hadLocalControl) {
                    SetCursorLocked(false);
                }
                horizontalVelocity = Vector3.zero;
                verticalVelocity = 0f;
            }
        }

        private void EnableInput() {
            DisableInput();
            if (inputActions == null) {
                Debug.LogError("First-person voice player needs an Input Action Asset.", this);
                return;
            }

            runtimeInputActions = Instantiate(inputActions);
            moveAction = runtimeInputActions.FindAction("Player/Move");
            lookAction = runtimeInputActions.FindAction("Player/Look");
            jumpAction = runtimeInputActions.FindAction("Player/Jump");
            sprintAction = runtimeInputActions.FindAction("Player/Sprint");
            menuAction = runtimeInputActions.FindAction("Player/Menu");

            if (moveAction == null || lookAction == null || jumpAction == null || sprintAction == null || menuAction == null) {
                Debug.LogError("First-person voice input is missing a required Player action.", this);
                DisableInput();
                return;
            }
            runtimeInputActions.Enable();
        }

        private void DisableInput() {
            if (runtimeInputActions != null) {
                runtimeInputActions.Disable();
                Destroy(runtimeInputActions);
            }

            runtimeInputActions = null;
            moveAction = null;
            lookAction = null;
            jumpAction = null;
            sprintAction = null;
            menuAction = null;
        }

        private void CacheComponentReferences() {
            if (characterController == null) {
                TryGetComponent(out characterController);
            }
            if (microphoneInput == null) {
                TryGetComponent(out microphoneInput);
            }
        }

        private void UpdateCursor() {
            if (menuAction != null && menuAction.WasPressedThisFrame()) {
                SetCursorLocked(!isCursorLocked);
                return;
            }

            Mouse mouse = Mouse.current;
            if (!isCursorLocked && mouse != null && mouse.leftButton.wasPressedThisFrame) {
                SetCursorLocked(true);
            }
        }

        private void UpdateLook() {
            if (!isCursorLocked || lookAction == null || cameraPivot == null) { return; }

            Vector2 lookInput = lookAction.ReadValue<Vector2>();
            InputControl activeControl = lookAction.activeControl;
            bool isPointerInput = activeControl != null && activeControl.device is Pointer;
            float lookScale = isPointerInput ? mouseLookSensitivity : gamepadLookSpeed * Time.deltaTime;
            float horizontalLook = lookInput.x * lookScale;
            float verticalLook = lookInput.y * lookScale;

            transform.Rotate(Vector3.up, horizontalLook, Space.World);
            cameraPitch = Mathf.Clamp(cameraPitch - verticalLook, -verticalLookLimit, verticalLookLimit);
            cameraPivot.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
        }

        private void UpdateMovement() {
            if (!characterController.enabled) { return; }

            Vector2 moveInput = GetMoveInput();
            bool isSprinting = sprintAction != null && sprintAction.IsPressed();
            float targetSpeed = isSprinting ? sprintSpeed : moveSpeed;
            Vector3 movementDirection = transform.right * moveInput.x + transform.forward * moveInput.y;
            if (movementDirection.sqrMagnitude > 1f) {
                movementDirection.Normalize();
            }

            Vector3 targetVelocity = movementDirection * targetSpeed;
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVelocity, speedChangeRate * Time.deltaTime);

            bool isGrounded = characterController.isGrounded;
            if (isGrounded && verticalVelocity < 0f) {
                verticalVelocity = groundedVelocity;
            }
            if (isGrounded && jumpAction != null && jumpAction.WasPressedThisFrame()) {
                verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }

            verticalVelocity = Mathf.Max(verticalVelocity + gravity * Time.deltaTime, -terminalVelocity);
            Vector3 movement = horizontalVelocity + Vector3.up * verticalVelocity;
            characterController.Move(movement * Time.deltaTime);

            if (playerAnimator != null) {
                float maximumSpeed = Mathf.Max(sprintSpeed, .01f);
                playerAnimator.SetFloat(SPEED_PARAMETER, horizontalVelocity.magnitude / maximumSpeed, .1f, Time.deltaTime);
            }
        }

        private void SetRenderersEnabled(bool shouldEnable) {
            if (localHiddenRenderers == null) { return; }

            for (int i = 0; i < localHiddenRenderers.Length; i++) {
                Renderer playerRenderer = localHiddenRenderers[i];
                if (playerRenderer != null) {
                    playerRenderer.enabled = shouldEnable;
                }
            }
        }

        private void SetCursorLocked(bool shouldLock) {
            isCursorLocked = shouldLock && hasLocalControl;
            Cursor.lockState = isCursorLocked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !isCursorLocked;
        }

        private Vector2 GetMoveInput() {
            if (moveAction == null) { return Vector2.zero; }

            Vector2 moveInput = moveAction.ReadValue<Vector2>();
            return Vector2.ClampMagnitude(moveInput, 1f);
        }

    }

}
