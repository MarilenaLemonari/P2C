using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
using RVO;
using Vector2 = UnityEngine.Vector2;

public class CustomSceneManager : MonoBehaviour
{
    public bool m_envReady = true;
    public List<Transform> m_interactionAreas;
    private int m_trajectoryIndex;
    [Header("Agent Parameters")] 
    private int m_minNumOfAgents = 20;
    private int m_maxNumOfAgents = 20;
    private int m_numOfAgents;
    [Header("Trajectories Parameters")]
    public bool m_saveTrajectories;
    [SerializeField] private float m_saveStartTime;
    [Header("CCP Parameters")]
    [SerializeField] private GameObject m_agentPrefab;

    [SerializeField] public float goalWeight;
    [SerializeField] public float groupWeight;
    [SerializeField] public float interWeight;
    [SerializeField] public float interconnWeight;
    [SerializeField] public float maxEnvironmentDistance;
    [SerializeField] public float goalDistanceThreshold;
    [SerializeField] public float interactionDistanceThreshold;
    private float m_neighbourDistanceThreshold = 4;
    public float stationaryVelocityThreshold = 0.1f;
    private Transform m_areaParent;
    [SerializeField] private LayerMask m_spawnLayerMask;
    [SerializeField] private Canvas m_loadingCanvas;

    [Header("Debugging")]
    private Transform m_agentsParent;
    private List<GameObject> m_agents;
    [HideInInspector] public List<GameObject> activeAgents;
    [HideInInspector] public List<GameObject> spawnedAgents;
    private ConsoleManager m_console;
    private List<float> m_envList;
    private List<ConsoleManager.EnvironmentGrid> m_sceneObjects;
    private List<BehaviorProfile> m_profiles;
    public Dictionary<int, InterconnectionData> interconnectionData;
    private SquareArea profileArea;
    [SerializeField] private bool m_isBehaviorDemo;
    private bool m_skipDemoDelay;
    public int globalFrame;
    
    public class SquareArea
    {
        public Vector3 center;
        public Vector2 halfExtent;

        public SquareArea(Vector3 center, Vector2 halfExtent)
        {
            this.center = center;
            this.halfExtent = halfExtent;
        }

        public bool Contains(Vector3 point)
        {
            return Mathf.Abs(point.x - center.x) < halfExtent.x && Mathf.Abs(point.z - center.z) < halfExtent.y;
        }
    }

    public class InterconnectionData
    {
        public int id;
        public Vector3 centerOfMass;
        public float averageDistance;
        public float speedVariance;
        public List<modCCPAgent> agents;
        private Color color;
        private SquareArea profileArea;
        public float initialSpeed;

        public InterconnectionData(int idIn, SquareArea area)
        {
            id = idIn;
            centerOfMass = Vector3.zero;
            averageDistance = 0;
            agents = new List<modCCPAgent>();
            color = new Color(Random.value, Random.value, Random.value, 1.0f);
            profileArea = area;
        }

        public int RemoveAgentFromGroup(modCCPAgent agent)
        {
            agents.Remove(agent);
            return agents.Count;
        }
        
        public void AddAgentToGroup(modCCPAgent agent) { agents.Add(agent); }
        
        public void UpdateInterconnectionParameters()
        {
            // Center of mass
            Vector3 center = new Vector3(0, 0, 0);
            foreach (modCCPAgent agent in agents)
            {
                center += agent.transform.position;
            }
            centerOfMass = center / agents.Count;

            // Center Distance
            float dis = 0;
            foreach (modCCPAgent agent in agents)
            {
                Debug.DrawLine(agent.transform.position,centerOfMass, color);
                dis += Vector3.Distance(agent.transform.position, centerOfMass);
            }
            averageDistance = dis / agents.Count;

            CalculateSpeedVariance();
            CheckProfileArea();
        }
        
        private void CalculateSpeedVariance()
        {
            float sum = 0;
            float mean = 0;
            float variance = 0;
            for (int i = 0; i < agents.Count; i++)
            {
                sum += agents[i].m_currentSpeed;
            }
            mean = sum / agents.Count;
            for (int i = 0; i < agents.Count; i++)
            {
                variance += (agents[i].m_currentSpeed - mean)
                            * (agents[i].m_currentSpeed - mean);
            }
            variance /= agents.Count;
            speedVariance = variance;
        }

        private void CheckProfileArea()
        {
            if(agents[0].inheritWeights)
                return;
            
            foreach (var agent in agents)
            {
                if(!profileArea.Contains(agent.transform.position))
                {
                    return;
                }
            }
            
            foreach (var agent in agents)
            {
                agent.inheritWeights = true;
            }
        }

        public void StartMoving()
        {
            UpdateInterconnectionParameters();
            foreach (var agent in agents)
            {
                agent.iSableToMove = true;
            }
        }
    }
    
    private void Awake()
    {
        m_console = GetComponent<ConsoleManager>();
        m_agentsParent = transform.Find("Agents");
        activeAgents = new List<GameObject>();
        spawnedAgents = new List<GameObject>();
        CreateAgents();
        Transform interactionParent = transform.Find("InteractionObjects");
        m_interactionAreas = new List<Transform>(interactionParent.childCount);
        foreach (Transform child in interactionParent) {
            m_interactionAreas.Add(child);
        }
        m_sceneObjects = new List<ConsoleManager.EnvironmentGrid>();
        interconnectionData = new Dictionary<int, InterconnectionData>();
        m_areaParent = transform.Find("SpawnAreas").transform;
        if(m_areaParent.childCount == 4)
            profileArea = new SquareArea(transform.position, new Vector2(6f, 6f));
        else
            profileArea = new SquareArea(transform.position, new Vector2(7f, 6f));
    }

    private void UpdateInterconnectionData()
    {
        foreach (var data in interconnectionData)
            data.Value.UpdateInterconnectionParameters();
    }
    
    public void RemoveAgent(modCCPAgent agent, int groupId)
    {
        if (agent.m_rvoID >= 0)
        {
            Simulator.Instance.delAgent(agent.m_rvoID);
            agent.m_rvoID = -1;
        }

        if (interconnectionData.Count > 0)
        {
            int groupCount = interconnectionData[groupId].RemoveAgentFromGroup(agent);
            if (groupCount == 0)
            {
                interconnectionData.Remove(groupId);
            }
        }
    }
    
    private void FixedUpdate()
    {
        UpdateInterconnectionData();
        globalFrame += 1;
    }

    public void StartNewDemo(BehaviorProfile profile)
    {
        m_skipDemoDelay = true;
        m_loadingCanvas.enabled = false;
        StartCoroutine(LoadingDemoScreen(2f));
        
        goalWeight = profile.goal;
        groupWeight = profile.group;
        interWeight = profile.interaction;
        interconnWeight = profile.connection;
        
        ReprocessDemoObstacle();
        SpawnAgents();
        StartCoroutine(ReloadEnvironmentDemo(15f));
    }

    private IEnumerator LoadingDemoScreen(float duration)
    {
        m_loadingCanvas.enabled = true;
        yield return new WaitForSeconds(duration);
        m_loadingCanvas.enabled = false;
    }

    public void StartNextSimulation()
    {
        m_envReady = false;
        globalFrame = 0;
        SpawnAgents();
        StartCoroutine(CallReload());        
    }
    
    private IEnumerator CallReload()
    {
        yield return StartCoroutine(ReloadEnvironment());
    }

    // Create agents and keep them deactivated to avoid repetitive Instantiate/Destroy
    private void CreateAgents()
    {
        m_agents = new List<GameObject>(m_maxNumOfAgents);
        for (int i = 0; i < m_maxNumOfAgents; i++)
        {
            GameObject agent = Instantiate(m_agentPrefab, Vector3.zero, Quaternion.identity, m_agentsParent);
            m_agents.Add(agent);
            agent.SetActive(false);
        }
    }

    // Deactivate all agents to respawn again
    private void DeactivateAgents()
    {
        for (int i = 0; i < m_maxNumOfAgents; i++)
        {
            m_agents[i].transform.localPosition = new Vector3(0f, 0f, 0f);
            m_agents[i].transform.position = Vector3.zero;
            m_agents[i].transform.localRotation = Quaternion.identity;
            modCCPAgent ccp = m_agents[i].GetComponent<modCCPAgent>();
            if (ccp.m_rvoID >= 0)
            {
                Simulator.Instance.delAgent(ccp.m_rvoID);
                ccp.m_rvoID = -1;
            }
            m_agents[i].SetActive(false);
        }
        activeAgents.Clear();
        activeAgents.TrimExcess();
        spawnedAgents.Clear();
        spawnedAgents.TrimExcess();
    }
    
    private void RandomizeObstacles()
    {
        m_sceneObjects.Clear();
        Transform intParent = transform.Find("InteractionObjects");
        string tag = "Interaction";

        Transform obj = intParent.GetChild(0);
        float x = UnityEngine.Random.Range(-2f, 2f);
        float z = UnityEngine.Random.Range(-2f, 2f);
        Vector3 pos = new Vector3(x, 2, z);
        obj.localPosition = pos;
        obj.localScale = new Vector3(UnityEngine.Random.Range(2f, 3.5f), 4f, UnityEngine.Random.Range(2f, 3.5f));
        float scaleDistance = 0.65f;
        if (interWeight < 0.2f)
            scaleDistance = 0.1f;
        
        float scaledDistanceX = scaleDistance / obj.localScale.x;
        float scaledDistanceZ = scaleDistance / obj.localScale.z;
        obj.GetComponent<BoxCollider>().size = 
            new Vector3(1.0f - 2 * scaledDistanceX, 1f, 1.0f - 2 * scaledDistanceZ);
        obj.gameObject.tag = tag;
        
        // If an interaction object is not needed remove it some times
        if (interWeight <= 0.1f)
        {
            obj.localPosition = new Vector3(obj.localPosition.x, -40f, obj.localPosition.z);
            return;
        }

        ConsoleManager.EnvironmentGrid eg = new ConsoleManager.EnvironmentGrid()
        {
            pos_x = obj.localPosition.x,
            pos_z = obj.localPosition.z,
            scale_x = obj.localScale.x,
            scale_z = obj.localScale.z,
            type = (obj.CompareTag("Obstacle")) ? 0.5f : 1f
        };
        m_sceneObjects.Add(eg);
    }

    private void ReprocessDemoObstacle()
    {
        MeshFilter box = m_interactionAreas[0].GetComponent<MeshFilter>();
        Vector3[] localVertices = box.mesh.vertices;
        
        IList<RVO.Vector2> obstacle = new List<RVO.Vector2>(localVertices.Length);
        
        // Directly convert local 3D vertices to world 3D, then to 2D
        for (int i = 0; i < localVertices.Length; i++)
        {
            Vector3 worldVertex = box.transform.TransformPoint(localVertices[i]);
            obstacle.Add(new RVO.Vector2(worldVertex.x, worldVertex.z));
        }
        Simulator.Instance.addObstacle(obstacle);
        Simulator.Instance.processObstacles();
    }
    
    private void SetRandomParameters()
    {
        float x = Random.Range(0f, 1f);
        float y = Random.Range(0f, 1f);

        // Sort x and y so we can partition the [0,1] range
        if (x > y)
        {
            float temp = x;
            x = y;
            y = temp;
        }

        goalWeight = x;
        groupWeight = y - x;
        interWeight = 1f - y;
        interconnWeight = 0.5f;
    }

    private void Update()
    {
        //Vector4 profile = InterpolateProfiles(Time.time / 90f);
        foreach (var a in activeAgents)
        {
            modCCPAgent ccp = a.GetComponent<modCCPAgent>();
            Vector4 profile = new Vector4(goalWeight, groupWeight, interWeight, interconnWeight);
            ccp.SetProfile(profile);
        }
    }

    public void SpawnAgents()
    {
        if (!m_isBehaviorDemo)
        {
            SetRandomParameters();
            RandomizeObstacles();
        }

        m_numOfAgents = UnityEngine.Random.Range(m_minNumOfAgents, m_maxNumOfAgents + 1);
        activeAgents.Clear();
        activeAgents.TrimExcess();
        spawnedAgents.Clear();
        spawnedAgents.TrimExcess();
        
        StartCoroutine(SpawnGradually(m_numOfAgents));
    }

    public IEnumerator SpawnGradually(int numberOfAgents)
    {
        if (!m_isBehaviorDemo){
            RVO_Manager_DataCollection rvoManager = RVO_Manager_DataCollection.Instance;
            while (rvoManager.obstaclesProcessed == false)
                yield return null;
        }
        
        List<int> availableKeys = Enumerable.Range(0, numberOfAgents + 1).ToList();
        interconnectionData = new Dictionary<int, InterconnectionData>();

        int i = 0;
        while(i < numberOfAgents)
        {
            int nextGroupCount = UnityEngine.Random.Range(2, 4);
            int groupId = availableKeys[0];
            availableKeys.Remove(groupId);
            float spawnAngle;
            Vector3[] positions;
            (spawnAngle, positions) = GetSpawnGoalPoints();
            Vector3 mainSpawnPos = positions[0];
            Vector3 mainExitPos = positions[1];
            Vector3[] spawnPositions = GenerateSpawnPointsAroundPoint(mainSpawnPos, nextGroupCount, spawnAngle);

            int nextIndexRange = nextGroupCount;
            if ((i + nextIndexRange) >= numberOfAgents)
                nextIndexRange = numberOfAgents - i;

            float groupInitialSpeed = 1f;
            if(!m_isBehaviorDemo)
                groupInitialSpeed = UnityEngine.Random.Range(0.5f, 1.5f);

            for (int j = 0; j < nextIndexRange; j++)
            {
                m_agents[i + j].transform.position = spawnPositions[j];
                activeAgents.Add(m_agents[i + j]);
                spawnedAgents.Add(m_agents[i + j]);
                modCCPAgent ccp = m_agents[i + j].GetComponent<modCCPAgent>();
                ccp.gameObject.SetActive(true);
                ccp.iSableToMove = false;
                ccp.inheritWeights = false;
                ccp.interpolateWeightsStart = false;
                Vector3 noise = new Vector3(
                    UnityEngine.Random.Range(-0.05f, 0.05f), 
                    0f,
                    UnityEngine.Random.Range(-0.05f, 0.05f)
                );
                int rvoID = Simulator.Instance.addAgent(new RVO.Vector2(spawnPositions[j].x, spawnPositions[j].z));
                ccp.m_rvoID = rvoID;
                ccp.SetGoalPosition(mainExitPos + noise);
                ccp.groupId = groupId;
                ccp.m_localFrame = 0;
                ccp.m_write = true;

                if (interconnectionData.ContainsKey(groupId))
                    interconnectionData[groupId].AddAgentToGroup(ccp);
                else
                {
                    InterconnectionData interData = new InterconnectionData(groupId, profileArea);
                    interData.AddAgentToGroup(ccp);
                    interData.initialSpeed = groupInitialSpeed;
                    interconnectionData.Add(groupId, interData);
                }
            }
            
            interconnectionData[groupId].StartMoving();

            i += nextGroupCount;
            
            float randomDelay = UnityEngine.Random.Range(0.75f, 1.5f);
            // Wait before starting taking screenshots
            yield return new WaitForSeconds(randomDelay);
        }
    }

    public Vector3[] GenerateSpawnPointsAroundPoint(Vector3 sourcePoint, int quantity, float spawnAngle)
    {
        Vector3[] retPoints = new Vector3[quantity];
        
        // If only one point need, return the center point
        if (quantity == 1){
            retPoints[0] = sourcePoint;
            return retPoints;
        }

        float[] distances = new[] {0, 1, -1f, 2, -2f};
        // Else return a list of points around the center point
        for (int i = 0; i < quantity; i++)
        {
            float angle = spawnAngle;
            float angleRad = Mathf.Deg2Rad * angle;
            float x = distances[i] * Mathf.Cos(angleRad);
            float z = distances[i] * Mathf.Sin(angleRad);
            Vector3 pointPos = new Vector3(x, 0, z);
            retPoints[i] = sourcePoint + pointPos;
        }
        return retPoints;
    }
    
    private bool IsAgentNearby(Vector3 position)
    {
        bool hit = Physics.CheckSphere(position, 2.0f, m_spawnLayerMask);
        return hit;
    }

    private (float, Vector3[]) GetSpawnGoalPoints()
    {
        Vector3[] positions = new Vector3[2];
        float spawnAngle = 0;
        
        List<Collider> areas = new List<Collider>(m_areaParent.childCount);
        foreach (Transform a in m_areaParent)
        {
            areas.Add(a.GetComponent<Collider>());
        }

        int maxTries = 100;
        // Select if current run will use all 4 spawn areas, or just 2 opposites
        // Spawn Point
        if (areas.Count == 2)
        {
            // Spawn Point
            int spawnIndex = UnityEngine.Random.Range(0, areas.Count);
            Bounds spawnBounds = areas[spawnIndex].bounds;
            // Ensure no other agents are in the selected spawn point
            do
            {
                positions[0] = new Vector3(
                    UnityEngine.Random.Range(spawnBounds.min.x, spawnBounds.max.x),
                    0f,
                    UnityEngine.Random.Range(spawnBounds.min.z, spawnBounds.max.z)
                );
                maxTries -= 1;
            } while (IsAgentNearby(positions[0]) && maxTries > 0);

            int goalIndex = (spawnIndex + 1) % 2;
            Bounds goalBounds = areas[goalIndex].bounds;
            positions[1] = new Vector3(
                UnityEngine.Random.Range(goalBounds.min.x, goalBounds.max.x),
                0f,
                UnityEngine.Random.Range(goalBounds.min.z, goalBounds.max.z)
            );
        }else if (areas.Count == 4)
        {
            // Spawn Point
            int spawnIndex = UnityEngine.Random.Range(0, areas.Count);
            Bounds spawnBounds = areas[spawnIndex].bounds;
            if (spawnIndex == 1 || spawnIndex == 3)
                spawnAngle = 90;
            do
            {
                positions[0] = new Vector3(
                    UnityEngine.Random.Range(spawnBounds.min.x, spawnBounds.max.x),
                    0f,
                    UnityEngine.Random.Range(spawnBounds.min.z, spawnBounds.max.z)
                );
                maxTries -= 1;
            } while (IsAgentNearby(positions[0]) && maxTries > 0);

            int goalIndex = (spawnIndex + 2) % 4;
            Bounds goalBounds = areas[goalIndex].bounds;
            positions[1] = new Vector3(
                UnityEngine.Random.Range(goalBounds.min.x, goalBounds.max.x),
                0f,
                UnityEngine.Random.Range(goalBounds.min.z, goalBounds.max.z)
            );
        }

        return (spawnAngle, positions);
    }

    // Take screenshots of the scene using all available cameras
    public IEnumerator ReloadEnvironment()
    {
        yield return new WaitForSeconds(m_saveStartTime);

        // Save trajectories of agents in csv files
        if(m_saveTrajectories)
            SaveTrajectories();
        
        // We can stop simulation and remove agents
        DeactivateAgents();
        
        m_envReady = true;
        yield break;
    }
    
    public IEnumerator ReloadEnvironmentDemo(float saveDelay)
    {
        float startTime = Time.time;
        yield return new WaitUntil(() => m_skipDemoDelay || Time.time - startTime >= saveDelay);
        
        m_skipDemoDelay = false;
        // We can stop simulation and remove agents
        DeactivateAgents();
        m_envReady = true;
        yield break;
    }

    //Save routes of every agent to .csv, if enabled
    public void SaveTrajectories()
    {
        ConsoleManager.ParametersGrid pg = new ConsoleManager.ParametersGrid()
        {
            goal = goalWeight,
            group = groupWeight,
            interaction = interWeight,
            interconn = interconnWeight
        };
        
        m_console.WriteEnvJson(pg, m_sceneObjects, gameObject.name, m_trajectoryIndex);
        
        int index = 0;
        foreach (var agent in spawnedAgents)
        {
            modCCPAgent ccp = agent.GetComponent<modCCPAgent>();
            ccp.m_write = false;
            if (ccp.timestepsList.Count > 10 && ccp.trajectoryList.Count > 10 && ccp.speedList.Count > 10 && ccp.rotationList.Count > 10)
            {
                int len = ccp.timestepsList.Count;
                if (ccp.trajectoryList.Count < len)
                    len = ccp.trajectoryList.Count; 
                if (ccp.speedList.Count < len)
                    len = ccp.speedList.Count;
                if (ccp.rotationList.Count < len)
                    len = ccp.rotationList.Count;

                m_console.WriteTrajectories(ccp.timestepsList, ccp.trajectoryList, ccp.speedList, ccp.rotationList,
                    transform.InverseTransformPoint(ccp.m_startingPos), transform.InverseTransformPoint(ccp.m_goalPos),
                    len, gameObject.name, m_trajectoryIndex, index);
                index += 1;
            }
            
            ccp.timestepsList.Clear();
            ccp.trajectoryList.Clear();
            ccp.speedList.Clear();
            ccp.rotationList.Clear();
            ccp.timestepsList.TrimExcess();
            ccp.trajectoryList.TrimExcess();
            ccp.speedList.TrimExcess();
            ccp.rotationList.TrimExcess();
        }

        m_trajectoryIndex += 1;
    }
    
    public float Normalize(float value, float minRange, float maxRange, float minDesired, float maxDesired)
    {
        float scaledValue = ((value - minRange) / (maxRange - minRange)) * (maxDesired - minDesired) + minDesired;
        return scaledValue;
    }
    private Vector3 CalculateGroupCenter(List<Vector3> points)
    {
        Vector3 center = new Vector3(0, 0, 0);
        float count = 0;
        foreach (Vector3 p in points)
        {
            center += p;
            count++;
        }
        return center / count;
    }

    public Vector3[] GetCloserAgent(Vector3 currentPos)
    {
        if (m_agents.Count < 2)
        {
            return new Vector3[]
            {
                currentPos,
                currentPos,
                new Vector3(0, 0, 0)
            };
        }
        
        List<Vector3> neighboursPoints = new List<Vector3>();
        
        int closeAgents = 0;
        Vector3 agentMin = currentPos;
        float minDist = Mathf.Infinity;

        foreach (GameObject otherAgent in activeAgents)
        {
            float dist = Vector3.Distance(otherAgent.transform.position, currentPos);
            if (dist <= m_neighbourDistanceThreshold)
            {
                closeAgents++;
                neighboursPoints.Add(otherAgent.transform.position);
            }
            
            if (dist < minDist && dist > 0.1f)
            {
                agentMin = otherAgent.transform.position;
                minDist = dist;
            }
        }

        Vector3 groupCenterPoint;
        if (neighboursPoints.Count > 1)
            groupCenterPoint = CalculateGroupCenter(neighboursPoints);
        else
            groupCenterPoint = currentPos;

        return new Vector3[]
        {
            agentMin,
            groupCenterPoint,
            new Vector3(closeAgents, 0f, 0f)
        };
    }
    
    public Vector3 GetCloserInteraction(Vector3 currentPos)
    {
        Vector3 objectMin = currentPos;
        float minDist = Mathf.Infinity;
        foreach (Transform obj in m_interactionAreas)
        {
            Collider objectCollider = obj.GetComponent<Collider>();
            Vector3 closestPointOnObject = objectCollider.ClosestPoint(currentPos);
            float distanceToEdge = Vector3.Distance(currentPos, closestPointOnObject);
            
            if (distanceToEdge < minDist)
            {
                objectMin = closestPointOnObject;
                minDist = distanceToEdge;
            }
        }

        return objectMin;
    }
    
    void OnDrawGizmos()
    {
        if (profileArea != null)
        {
            Gizmos.color = Color.black;
            Gizmos.DrawWireCube(profileArea.center,
                new Vector3(profileArea.halfExtent.x * 2f, 0.1f, profileArea.halfExtent.y * 2f));
        }
    }
}
