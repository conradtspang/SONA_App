using UnityEngine;

public class ObstacleKalmanFilter
{
    // State: [x, y, vx, vy]
    // Measurement: [x, y]

    private float[] stateVector; // Renamed from 'x' to avoid ambiguity
    private float[,] P; // Covariance matrix
    private float[,] F; // State transition matrix
    private float[,] H; // Measurement matrix
    private float[,] R; // Measurement noise covariance
    private float[,] Q; // Process noise covariance

    public ObstacleKalmanFilter(Vector2 initialPos)
    {
        // Initial State
        stateVector = new float[] { initialPos.x, initialPos.y, 0, 0 };

        // Initial Covariance (High uncertainty for velocity)
        P = new float[,] {
            { 1, 0, 0,    0 },
            { 0, 1, 0,    0 },
            { 0, 0, 1000, 0 },
            { 0, 0, 0,    1000 }
        };

        // State Transition (Initialized with dt=0, updated every step)
        F = new float[,] {
            { 1, 0, 1, 0 },
            { 0, 1, 0, 1 },
            { 0, 0, 1, 0 },
            { 0, 0, 0, 1 }
        };

        // Measurement Matrix (We observe x and y)
        H = new float[,] {
            { 1, 0, 0, 0 },
            { 0, 1, 0, 0 }
        };

        // Measurement Noise (Sensor uncertainty)
        R = new float[,] {
            { 10, 0 },
            { 0, 10 }
        };

        // Process Noise (Model uncertainty)
        Q = new float[,] {
            { 1, 0, 0, 0 },
            { 0, 1, 0, 0 },
            { 0, 0, 10, 0 },
            { 0, 0, 0, 10 }
        };
    }

    public Vector2 Predict(float dt)
    {
        // Update F with dt
        F[0, 2] = dt;
        F[1, 3] = dt;

        // x = F * x
        float[] newState = new float[4];
        newState[0] = F[0, 0] * stateVector[0] + F[0, 1] * stateVector[1] + F[0, 2] * stateVector[2] + F[0, 3] * stateVector[3];
        newState[1] = F[1, 0] * stateVector[0] + F[1, 1] * stateVector[1] + F[1, 2] * stateVector[2] + F[1, 3] * stateVector[3];
        newState[2] = F[2, 0] * stateVector[0] + F[2, 1] * stateVector[1] + F[2, 2] * stateVector[2] + F[2, 3] * stateVector[3];
        newState[3] = F[3, 0] * stateVector[0] + F[3, 1] * stateVector[1] + F[3, 2] * stateVector[2] + F[3, 3] * stateVector[3];
        stateVector = newState;

        // P = F * P * F^T + Q
        P = MatrixAdd(MatrixMultiply(MatrixMultiply(F, P), Transpose(F)), Q);

        return new Vector2(stateVector[0], stateVector[1]);
    }

    public void Update(Vector2 measurement)
    {
        float[] z = new float[] { measurement.x, measurement.y };

        // y = z - H * x (Innovation)
        float[] Hx = new float[] {
            H[0, 0] * stateVector[0] + H[0, 1] * stateVector[1] + H[0, 2] * stateVector[2] + H[0, 3] * stateVector[3],
            H[1, 0] * stateVector[0] + H[1, 1] * stateVector[1] + H[1, 2] * stateVector[2] + H[1, 3] * stateVector[3]
        };
        float[] y = new float[] { z[0] - Hx[0], z[1] - Hx[1] };

        // S = H * P * H^T + R (Innovation Covariance)
        float[,] S = MatrixAdd(MatrixMultiply(MatrixMultiply(H, P), Transpose(H)), R);

        // K = P * H^T * S^-1 (Kalman Gain)
        float[,] K = MatrixMultiply(MatrixMultiply(P, Transpose(H)), Invert2x2(S));

        // x = x + K * y
        stateVector[0] += K[0, 0] * y[0] + K[0, 1] * y[1];
        stateVector[1] += K[1, 0] * y[0] + K[1, 1] * y[1];
        stateVector[2] += K[2, 0] * y[0] + K[2, 1] * y[1];
        stateVector[3] += K[3, 0] * y[0] + K[3, 1] * y[1];

        // P = (I - K * H) * P
        float[,] I = Identity(4);
        float[,] KH = MatrixMultiply(K, H);
        P = MatrixMultiply(MatrixSubtract(I, KH), P);
    }

    public Vector2 GetPosition()
    {
        return new Vector2(stateVector[0], stateVector[1]);
    }

    public Vector2 GetVelocity()
    {
        return new Vector2(stateVector[2], stateVector[3]);
    }

    // --- Simple Matrix Utils ---

    private float[,] MatrixMultiply(float[,] A, float[,] B)
    {
        int rA = A.GetLength(0);
        int cA = A.GetLength(1);
        int cB = B.GetLength(1);
        float[,] C = new float[rA, cB];

        for (int i = 0; i < rA; i++)
            for (int j = 0; j < cB; j++)
                for (int k = 0; k < cA; k++)
                    C[i, j] += A[i, k] * B[k, j];
        return C;
    }

    private float[,] Transpose(float[,] A)
    {
        int r = A.GetLength(0);
        int c = A.GetLength(1);
        float[,] T = new float[c, r];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                T[j, i] = A[i, j];
        return T;
    }

    private float[,] MatrixAdd(float[,] A, float[,] B)
    {
        int r = A.GetLength(0);
        int c = A.GetLength(1);
        float[,] C = new float[r, c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                C[i, j] = A[i, j] + B[i, j];
        return C;
    }
    
    private float[,] MatrixSubtract(float[,] A, float[,] B)
    {
        int r = A.GetLength(0);
        int c = A.GetLength(1);
        float[,] C = new float[r, c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                C[i, j] = A[i, j] - B[i, j];
        return C;
    }

    private float[,] Identity(int size)
    {
        float[,] I = new float[size, size];
        for (int i = 0; i < size; i++) I[i, i] = 1;
        return I;
    }

    private float[,] Invert2x2(float[,] M)
    {
        float det = M[0, 0] * M[1, 1] - M[0, 1] * M[1, 0];
        float invDet = 1.0f / det;
        return new float[,] {
            { M[1, 1] * invDet, -M[0, 1] * invDet },
            { -M[1, 0] * invDet, M[0, 0] * invDet }
        };
    }
}