using Dreamteck.Splines;
using Evaluation;
using System;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using Unity.Vehicles;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using VehiclePhysics;
using static VehiclePhysics.VehicleBase;

//Reward terms, each summed over the episode and sent to TensorBoard as Reward/<term> (same names as the Arcade project, plus Crash, Speed and Idle)
public enum RewardTerm { Progress, Time, Checkpoints, Finish, ObstacleContact, ObstacleHits, OffTrack, OffTrackEnd, Crash, Speed, Idle }

public class AgentML : Agent
{
    private CarInterface m_carInterface;
    private InputCarPlayer m_carPlayer;
    private Rigidbody m_rb;
    private Vector3 m_startPosition;
    private Quaternion m_startRotation;

    [SerializeField] private EnvTraining m_envTraining;

    [Header("Checkpoints")]
    public List<Checkpoint> checkpoints;
    private int m_maxCheckpointCount;
    private Checkpoint m_currentCheckpoint;

    [Header("Active Roads")]
    private List<Road> m_roads;

    [Header("Track")]
    [SerializeField] private List<SplineComputer> m_splines;
    [Tooltip("Distance from the spline (m) beyond which the episode ends")]
    [SerializeField] private float m_maxDistanceFromSpline = 15f;
    private float m_previousDistanceAlongTrack = 0f;
    [SerializeField] private int m_groundType;

    [Header("Rewards")]
    [SerializeField] private float m_checkpointReward = 1.0f;
    [SerializeField] private float m_lastCheckpointReward = 1.0f;
    [Tooltip("Reward for each meter of progress along the splines")]
    [SerializeField] private float m_progressRewardPerMeter = 0.001f;
    [Tooltip("Off-track penalty scale: each step (50 per second) beyond the road half-width costs this x ratio / MaxStep, the ratio going from 1 at the road edge to 2 at the maximum distance from the spline. With MaxStep 5000, 1 costs 0.01 per second at the road edge")]
    [SerializeField] private float m_splineDistancePenaltyMultiplier = 2.0f;
    [SerializeField] private bool m_enableIdlePenalty = false;
    [SerializeField] private float m_idlePenality = 2.5f;
    [SerializeField] private float m_idlePenalityMinThreshold = 0.5f;
    [SerializeField] private float m_crashPenalty = -1.0f;
    [Tooltip("Given once per obstacle hit in the episode, on top of the collision rule's penalty while in contact")]
    [SerializeField] private float m_obstacleHitPenalty = -0.2f;
    [SerializeField] private bool m_enableSpeedReward = false;
    [SerializeField] private bool m_enableFrictionOffRoad = true;
    [SerializeField] private bool m_enableTimePenalty = true;
    [SerializeField] private float m_maxTimePenalty = 1.0f;

    [Header("Optional actions")]
    [Tooltip("Discrete action 0. The action branch always exists (same action layout as the Arcade project) but is ignored while this is off")]
    [SerializeField] private bool m_enableHandbrake = false;
    [Tooltip("Discrete action 1. The car has no boost mechanic yet: for now this only gates the action and the boost observation")]
    [SerializeField] private bool m_enableBoost = false;

    public bool IsHandbrakeEnabled => m_enableHandbrake;
    public bool IsBoostEnabled => m_enableBoost;
    //Discrete actions of the last step, always false while their option is off
    public bool HandbrakeAction { get; private set; }
    public bool BoostAction { get; private set; }

    [Header("HUD")]
    [Tooltip("Shows the throttle / brake action of the agent on screen (pedal icon, like the Arcade project)")]
    [SerializeField] private bool m_showHud = true;


    private const float m_checkpointRewardValue = 1.0f;

    public float LastCheckpointReward => m_lastCheckpointReward;

    //Throttle (positive) / brake (negative) action of the last step, from -1 to 1
    public float ThrottleAction { get; private set; }

    public System.Action<bool> OnEndEpisode;
    [Header("Normale parameter")]
    [SerializeField] private LayerMask m_layerMask;
    private Vector3 normalGround;
    private int m_groundId;
    [System.Serializable]
    public class CollisionReward
    {
        public string objectTag = "Obstacle";
        public bool giveReward = true;
        public float rewardAmount = -1f;
        public bool endEpisode = true;
    }

    private List<GameObject> m_uniqueHitObstacle = new List<GameObject>();
    private List<GameObject> m_hitObjects = new List<GameObject>();
    [Header("Collision Rules")]
    [SerializeField] private List<CollisionReward> m_collisionRules = new List<CollisionReward>();
    private const float m_maxSpeed = 35.0f;

    [Header("Wheel Control")]
    public VPVehicleController vehicle;
    private WheelState[] m_tireFrictions;
    [SerializeField] private float m_roadGrip = 1f;
    [SerializeField] private float m_offroadGrip = 0.2f;
    [SerializeField] private float m_roadDrag = 0f;
    [SerializeField] private float m_offroadDrag = 1f;


    [Header("Observation")]
    private const float m_maxDistanceOberservationMesure = 500.0f;
    //Same distances as the Arcade project, far enough to see a corner before the braking point at top speed
    private static readonly float[] m_lookAheadDistances = { 25.0f, 50.0f, 100.0f, 150.0f };

    [Tooltip("Half-width of the road around the spline (m): the distance-to-spline observation is divided by it and the off-track penalty starts beyond it")]
    [SerializeField] private float m_constSizeRoad = 15.0f;

    private int m_episodeStepCount = 0;
    private int m_episodeOffRoadSteps = 0;
    private float m_episodeBrakeInputSum = 0f;
    private readonly float[] m_episodeRewards = new float[Enum.GetValues(typeof(RewardTerm)).Length];


    // --- Input Var ---
    private const int m_boostInputValue = 0;



    #region Unity Function
    public void Start()
    {
        var wheelData = vehicle.wheelState;
        m_carInterface = GetComponent<CarInterface>();
        m_carPlayer = GetComponent<InputCarPlayer>();
        m_rb = GetComponent<Rigidbody>();
        m_startPosition = transform.position;
        m_startRotation = transform.rotation;
        foreach (WheelState tire in vehicle.wheelState)
        {
            tire.groundMaterial = new GroundMaterial();
        }

        EvaluationTests.SetAgentTrainingStepValue(CompletedEpisodes);
        EvaluationTests.GetEventTimeTrialEnd()?.AddListener(ResetAgent);

        CheckBehaviorParameters();

        if (m_showHud && GetComponent<HUD_Car>() == null)
            gameObject.AddComponent<HUD_Car>().Initialize(this);
    }

    //The optional actions change the observation / action sizes, which the Behavior Parameters don't follow by themselves
    private void CheckBehaviorParameters()
    {
        BehaviorParameters behaviorParameters = GetComponent<BehaviorParameters>();
        if (behaviorParameters == null)
            return;

        //Velocity 3, direction to the checkpoint 3, ground type 1, offset to the spline 3, look-ahead points 3 each, ground normal 3, plus the boost gauge when boost is on
        int expectedObservations = 13 + 3 * m_lookAheadDistances.Length + (m_enableBoost ? 1 : 0);
        if (behaviorParameters.BrainParameters.VectorObservationSize != expectedObservations)
        {
            Debug.LogError("AgentML: Behavior Parameters 'Space Size' is " + behaviorParameters.BrainParameters.VectorObservationSize + " but the agent collects " + expectedObservations
                + " observations (boost " + (m_enableBoost ? "on" : "off") + ")");
        }

        //Branch 0 is the handbrake, branch 1 the boost: boost needs both branches, handbrake only the first
        int neededBranches = m_enableBoost ? 2 : (m_enableHandbrake ? 1 : 0);
        int branches = behaviorParameters.BrainParameters.ActionSpec.BranchSizes.Length;
        if (branches < neededBranches)
        {
            Debug.LogError("AgentML: Behavior Parameters have " + branches + " discrete branches but the enabled options need " + neededBranches
                + " (branch 0 = handbrake, branch 1 = boost, 2 choices each)");
        }
    }

    public void FixedUpdate()
    {
        RaycastHit hit = new RaycastHit();
        if (Physics.Raycast(transform.position, Vector3.down, out hit, 1.5f, m_layerMask))
        {
            normalGround = hit.normal;
            if (hit.collider.tag == "Road")
            {
                EvaluationTests.SetAgentOffRoad(false);
                m_groundId = 1;
                if (vehicle && m_enableFrictionOffRoad)
                    SetRoadFriction();
            }
            else
            {
                EvaluationTests.SetAgentOffRoad(true);
                m_groundId = 2;
                if (vehicle && m_enableFrictionOffRoad)
                    SetOffroadFriction();
            }
        }
        else
        {
            normalGround = transform.up;
            m_groundId = 0;
        }
    }
    #endregion

    #region MLAgent Functions
    public override void OnEpisodeBegin()
    {
        RecordEpisodeStats();

        // Reset input car
        m_carInterface.SetThrottleValue(0.0f);
        m_carInterface.SetBrakeValue(0.0f);
        m_carInterface.SetSteeringValue(0.0f);

        // Set max speed evaluation data
        EvaluationTests.SetAgentMaxSpeed(m_maxSpeed);

        m_uniqueHitObstacle.Clear();

        m_envTraining.ResetEnvTraining();

        checkpoints = new List<Checkpoint>(m_envTraining.GetCheckpointList());
        m_maxCheckpointCount = checkpoints.Count;

        m_splines = m_envTraining.GetSplineList();

        m_currentCheckpoint = checkpoints[0];

        m_hitObjects.Clear();

        m_previousDistanceAlongTrack = GetDistanceAlongTrack();
    }



    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.InverseTransformDirection(m_rb.linearVelocity / m_maxSpeed));

        Vector3 targetDirection = m_currentCheckpoint != null ? m_currentCheckpoint.transform.position - transform.position : Vector3.zero;
        sensor.AddObservation(transform.InverseTransformDirection(targetDirection) / m_maxDistanceOberservationMesure);

        sensor.AddObservation(m_groundId);
        //Only present while boost is enabled, like the Arcade project: the Behavior Parameters observation size follows (see CheckBehaviorParameters)
        if (m_enableBoost)
            sensor.AddObservation(m_carInterface.GetBoostAmount());

        Vector3 toSpline0 = transform.InverseTransformDirection((GetClosetSplinePoint() - transform.position) / m_constSizeRoad);
        sensor.AddObservation(toSpline0);

        //Each point is normalized by its own distance, so the far points are as readable as the close ones
        foreach (float distance in m_lookAheadDistances)
        {
            Vector3 toSplinePoint = transform.InverseTransformDirection((GetSplinePointInFront(distance) - transform.position) / distance);
            sensor.AddObservation(toSplinePoint);
        }

        sensor.AddObservation(normalGround.normalized);
    }

    // Action Buffer
    // - Continous Action 0: Steering
    // - Continuous Action 1 : Throttle / Brake
    // - Discrete Action 0 : Handbrake (optional, m_enableHandbrake)
    // - Discrete Action 1 : Boost (optional, m_enableBoost)
    public override void OnActionReceived(ActionBuffers actions)
    {
        float throttleValue = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);
        ThrottleAction = throttleValue;

        if (throttleValue >= 0)
        {
            m_carInterface.SetThrottleValue(throttleValue);
            m_carInterface.SetBrakeValue(0.0f);

        }
        else
        {
            m_carInterface.SetBrakeValue(throttleValue);
            m_carInterface.SetThrottleValue(0.0f);
        }

        m_episodeStepCount++;
        if (m_groundId == 2)
            m_episodeOffRoadSteps++;
        m_episodeBrakeInputSum += Mathf.Max(0.0f, -throttleValue);

        float steeringValue = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        m_carInterface.SetSteeringValue(steeringValue);

        //Discrete actions are ignored (always off) while their option is disabled
        HandbrakeAction = m_enableHandbrake && actions.DiscreteActions.Length > 0 && actions.DiscreteActions[0] == 1;
        BoostAction = m_enableBoost && actions.DiscreteActions.Length > 1 && actions.DiscreteActions[1] == 1;
        m_carInterface.ActiveHandbrake(HandbrakeAction);

        float progressReward = HandleProgressReward();
        float splinePenalty = HandleSplinePenalty();
        float speedReward = 0;
        float timePenalty = 0;
        float ildePenalty = 0;

        if (m_enableIdlePenalty)
            ildePenalty = HandleIdlePenalty();
        if (m_enableSpeedReward)
            speedReward = HandleSpeedReward();
        if (m_enableTimePenalty)
            timePenalty = HandleTimePenality();

        foreach (var hit in m_hitObjects)
        {
            HandleCollision(hit);
        }

        EvaluationTests.SetAgentSpeed(m_rb.linearVelocity.magnitude);

    }



    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continousAction = actionsOut.ContinuousActions;
        continousAction[1] = m_carPlayer.GetThrottleValue();
        continousAction[0] = m_carPlayer.GetSteeringValue();

        var discreteAction = actionsOut.DiscreteActions;
        if (m_enableHandbrake && discreteAction.Length > 0)
            discreteAction[0] = m_carPlayer.GetHandbrakeValue() ? 1 : 0;
        //No boost input on the keyboard yet
        if (m_enableBoost && discreteAction.Length > 1)
            discreteAction[1] = m_boostInputValue;
    }
    #endregion

    #region Collisions Functions
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.tag == "Obstacle" && !m_hitObjects.Contains(collision.gameObject))
        {
            m_hitObjects.Add(collision.gameObject);
            if (!m_uniqueHitObstacle.Contains(collision.gameObject))
            {
                m_uniqueHitObstacle.Add(collision.gameObject);
                EvaluationTests.AddHitObjstacle();
                AddReward(RewardTerm.ObstacleHits, m_obstacleHitPenalty);
                EvaluationTests.SetAgentIsColliding(true);
            }

        }
    }

    private void OnCollisionExit(Collision collision)
    {
        if (m_hitObjects.Contains(collision.gameObject))
        {
            EvaluationTests.SetAgentIsColliding(false);
            m_hitObjects.Remove(collision.gameObject);
        }
    }

    //Ground ID (set in FixedUpdate)
    // 0 is not touching the ground
    // 1 is on the road
    // 2 is off road

    private void OnTriggerEnter(Collider other)
    {
        if (checkpoints == null)
            return;

        if (m_currentCheckpoint != null && other.transform == m_currentCheckpoint.transform)
        {
            AddCheckpointReward();

            if (!m_currentCheckpoint.m_isEnd)
            {
                EvaluationTests.AddAgentCheckpoint();
            }

            checkpoints.Remove(m_currentCheckpoint);
            //Empty after the last checkpoint: its reward was given here, so CrossLastCheckpoint won't give it again
            m_currentCheckpoint = checkpoints.Count > 0 ? checkpoints[0] : null;
        }
    }

    public void CrossLastCheckpoint()
    {
        if (m_currentCheckpoint != null)
        {
            AddCheckpointReward();
            checkpoints.Remove(m_currentCheckpoint);
            m_currentCheckpoint = null;
        }
    }

    //Full reward on the road, half off-road (same rule as the Arcade project)
    private void AddCheckpointReward()
    {
        float checkpointReward = m_checkpointRewardValue / m_maxCheckpointCount;
        AddReward(RewardTerm.Checkpoints, m_groundId == 2 ? checkpointReward / 2.0f : checkpointReward);
    }
    #endregion

    #region GetInfos Functions

    private SplineComputer GetClosestSpline()
    {
        if (m_splines.Count == 0) return null;

        SplineComputer closestSpline = m_splines[0];
        float closestDistance = Vector3.Distance(closestSpline.Project(transform.position).position, this.transform.position);
        foreach (SplineComputer spline in m_splines)
        {
            float distanceTest = Vector3.Distance(spline.Project(transform.position).position, this.transform.position);

            if (closestDistance > distanceTest)
            {
                closestSpline = spline;
                closestDistance = distanceTest;
            }
        }
        return closestSpline;

    }

    private float GetDistanceToSpline()
    {
        if (GetClosestSpline())
        {
            SplineSample splineSample = GetClosestSpline().Project(transform.position);
            float distance = Vector3.Distance(
                transform.position,
                splineSample.position
            );
            return distance;
        }
        return 0f;
    }
    private Vector3 GetClosetSplinePoint()
    {
        SplineComputer ClosestSpline = GetClosestSpline();
        if (ClosestSpline)
        {
            return ClosestSpline.Project(transform.position).position;
        }
        return Vector3.zero;
    }

    private Vector3 GetSplinePointInFront(float distance)
    {
        SplineComputer Spline = GetClosestSpline();
        if (Spline)
        {
            int splineIndex = m_splines.IndexOf(Spline);

            //Travel walks the real distance along the spline (the percent is not proportional to the distance)
            double percentInFront = Spline.Travel(Spline.Project(transform.position).percent, distance, out float moved);
            float remainingDistance = distance - moved;

            //Continue on the next splines while the target is past the end of the current one
            while (remainingDistance > 0.001f && splineIndex < m_splines.Count - 1)
            {
                splineIndex++;
                Spline = m_splines[splineIndex];
                percentInFront = Spline.Travel(0.0, remainingDistance, out moved);
                remainingDistance -= moved;
            }

            return Spline.Evaluate(percentInFront).position;
        }

        return Vector3.zero;
    }

    //Distance from the start of the track, measured along the splines
    private float GetDistanceAlongTrack()
    {
        float distanceAlongTrack = 0f;
        float splinesLengthBefore = 0f;
        float closestDistance = float.MaxValue;

        foreach (SplineComputer spline in m_splines)
        {
            SplineSample projection = spline.Project(transform.position);
            float distanceTest = Vector3.Distance(projection.position, transform.position);
            if (distanceTest < closestDistance)
            {
                closestDistance = distanceTest;
                distanceAlongTrack = splinesLengthBefore + spline.CalculateLength(0.0, projection.percent);
            }

            splinesLengthBefore += spline.CalculateLength();
        }

        return distanceAlongTrack;
    }

    #endregion

    #region Handling Functions

    //Meters gained along the splines since the last step (negative when going backwards)
    private float HandleProgressReward()
    {
        float distanceAlongTrack = GetDistanceAlongTrack();
        float progress = distanceAlongTrack - m_previousDistanceAlongTrack;
        m_previousDistanceAlongTrack = distanceAlongTrack;

        //Called every physics step, so a bigger progress is a jump of the projection (e.g. to another part of the track close to the car)
        float progressReward = 0;
        if (Mathf.Abs(progress) <= 2.0f * m_maxSpeed * Time.fixedDeltaTime)
        {
            progressReward = progress * m_progressRewardPerMeter;
            AddReward(RewardTerm.Progress, progressReward);
        }

        return progressReward;
    }

    //Adds the reward and its share to the episode total of its term
    public void AddReward(RewardTerm term, float reward)
    {
        AddReward(reward);
        m_episodeRewards[(int)term] += reward;
    }

    private void RecordEpisodeStats()
    {
        if (m_episodeStepCount > 0)
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;
            //Average brake input (0 to 1) of the previous episode, visible in TensorBoard
            stats.Add("Car/Brake Input", m_episodeBrakeInputSum / m_episodeStepCount);
            //Share of the episode spent on the off-road ground (grip loss and off-track penalty), to tune the penalty
            stats.Add("Car/Off Road Ratio", (float)m_episodeOffRoadSteps / m_episodeStepCount);

            //Total of each reward term over the episode: the Reward/ curves add up to Environment/Cumulative Reward
            for (int i = 0; i < m_episodeRewards.Length; i++)
            {
                RewardTerm term = (RewardTerm)i;
                bool isDisabled = (term == RewardTerm.Time && !m_enableTimePenalty)
                    || (term == RewardTerm.Speed && !m_enableSpeedReward)
                    || (term == RewardTerm.Idle && !m_enableIdlePenalty);
                if (!isDisabled)
                    stats.Add("Reward/" + term, m_episodeRewards[i]);
            }
        }

        m_episodeStepCount = 0;
        m_episodeOffRoadSteps = 0;
        m_episodeBrakeInputSum = 0f;
        Array.Clear(m_episodeRewards, 0, m_episodeRewards.Length);
    }

    //Penalty when you go far from the spline - to test might be too strict
    private float HandleSplinePenalty()
    {
        float distance = GetDistanceToSpline();
  
        float ratioValue = 0;
        if (distance < m_constSizeRoad)
        {
            return 0;
        }
        else
        {
            ratioValue = -1.0f * (1.0f + ((distance - m_constSizeRoad) / (m_maxDistanceFromSpline - m_constSizeRoad)));
        }

        AddReward(RewardTerm.OffTrack, (m_splineDistancePenaltyMultiplier * ratioValue) / (float)MaxStep);

        if (distance > m_maxDistanceFromSpline)
        {
            AddReward(RewardTerm.OffTrackEnd, -2f);
            EvaluationTests.FinishTrial(true);
            OnEndEpisode?.Invoke(false);
            EndEpisode();
        }

        return (m_splineDistancePenaltyMultiplier * ratioValue) / (float)MaxStep;
    }

    //To prevent car from not moving
    private float HandleIdlePenalty()
    {
        float penalty = 0;
        if (m_rb.linearVelocity.magnitude < m_idlePenalityMinThreshold)
            penalty = -m_idlePenality / (float)MaxStep;
        AddReward(RewardTerm.Idle, penalty);
        return penalty;
    }

    private void HandleCollision(GameObject hitObject)
    {
        if (hitObject && hitObject.activeInHierarchy)
        {
            foreach (var rule in m_collisionRules)
            {
                if (hitObject.CompareTag(rule.objectTag))
                {
                    if (rule.giveReward)
                    {
                        AddReward(RewardTerm.ObstacleContact, rule.rewardAmount / (float)MaxStep);
                    }

                    if (rule.endEpisode)
                    {
                        OnEndEpisode?.Invoke(false);
                        EvaluationTests.FinishTrial(true);
                        EndEpisode();
                    }

                    return;
                }
            }
        }
    }
    private float HandleSpeedReward()
    {
        float reward = Mathf.Clamp01(((m_rb.linearVelocity.magnitude / m_maxSpeed)) / (float)MaxStep);
        AddReward(RewardTerm.Speed, reward);
        return reward;
    }

    private float HandleTimePenality()
    {
        float penalty = -m_maxTimePenalty / (float)MaxStep;
        AddReward(RewardTerm.Time, penalty);
        return penalty;
    }
    #endregion

    public void ResetAgent()
    {
        AddReward(RewardTerm.Crash, m_crashPenalty);
        EvaluationTests.FinishTrial(true);
        OnEndEpisode?.Invoke(false);
        EndEpisode();
    }
    void SetRoadFriction()
    {
        foreach (WheelState tire in vehicle.wheelState)
        {
            tire.groundMaterial.grip = m_roadGrip;
            tire.groundMaterial.drag = m_roadDrag;
        }
    }
    void SetOffroadFriction()
    {
        foreach (WheelState tire in vehicle.wheelState)
        {
            tire.groundMaterial.grip = m_offroadGrip;
            tire.groundMaterial.drag = m_offroadDrag;
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Draw the closest spline point (toSpline0)
        Vector3 closestPoint = GetClosetSplinePoint();
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(closestPoint, 0.5f);
        Gizmos.DrawLine(transform.position, closestPoint);

        // Draw the look-ahead points
        Gizmos.color = Color.yellow;
        foreach (float distance in m_lookAheadDistances)
        {
            Vector3 pointInFront = GetSplinePointInFront(distance);
            Gizmos.DrawWireSphere(pointInFront, 0.5f);
            Gizmos.DrawLine(transform.position, pointInFront);
        }
    }

}
