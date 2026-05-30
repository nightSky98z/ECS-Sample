using NUnit.Framework;
using Unity.Mathematics;

public sealed class MovementMathTests
{
    [Test]
    public void ComposeVelocityAddsInputAndKnockback()
    {
        var input = new float2(1f, 0f);
        var knockback = new KnockbackVelocity
        {
            Value = new float3(0f, 0f, -3f),
            DecayPerSecond = 10f
        };

        var velocity = MovementMath.ComposeVelocity(input, 5f, knockback.Value);

        Assert.AreEqual(new float3(5f, 0f, -3f), velocity);
    }

    [Test]
    public void DecayKnockbackMovesTowardZeroWithoutFlippingDirection()
    {
        var velocity = new float3(0f, 0f, -3f);

        var decayed = MovementMath.DecayVelocity(velocity, 10f, 0.2f);
        var consumed = MovementMath.DecayVelocity(velocity, 10f, 1f);

        Assert.AreEqual(new float3(0f, 0f, -1f), decayed);
        Assert.AreEqual(float3.zero, consumed);
    }

    [Test]
    public void ResolveCirclePenetrationMovesPositionToCombinedRadius()
    {
        var movingPosition = new float3(0.5f, 2f, 0f);
        var blockingPosition = new float3(0f, 0f, 0f);

        var resolvedPosition = MovementMath.ResolveCirclePenetration(
            movingPosition,
            0.5f,
            blockingPosition,
            0.75f);

        Assert.AreEqual(new float3(1.25f, 2f, 0f), resolvedPosition);
    }

    [Test]
    public void ResolveCirclePenetrationKeepsSeparatedPosition()
    {
        var movingPosition = new float3(3f, 2f, 0f);
        var blockingPosition = new float3(0f, 0f, 0f);

        var resolvedPosition = MovementMath.ResolveCirclePenetration(
            movingPosition,
            0.5f,
            blockingPosition,
            0.75f);

        Assert.AreEqual(movingPosition, resolvedPosition);
    }

    [Test]
    public void ResolveCirclePenetrationHandlesMatchingCenters()
    {
        var movingPosition = new float3(0f, 2f, 0f);
        var blockingPosition = new float3(0f, 0f, 0f);

        var resolvedPosition = MovementMath.ResolveCirclePenetration(
            movingPosition,
            0.5f,
            blockingPosition,
            0.75f);

        Assert.AreEqual(new float3(1.25f, 2f, 0f), resolvedPosition);
    }

    [Test]
    public void CalculateChaseVelocityMovesTowardTargetOnXZPlane()
    {
        var velocity = MonsterSimpleAiMath.CalculateChaseVelocity(
            new float3(0f, 10f, 0f),
            new float3(3f, -10f, 4f),
            2f);

        Assert.AreEqual(new float3(1.2f, 0f, 1.6f), velocity);
    }

    [Test]
    public void CalculateChaseVelocityStopsAtTarget()
    {
        var velocity = MonsterSimpleAiMath.CalculateChaseVelocity(
            new float3(1f, 2f, 3f),
            new float3(1f, 9f, 3f),
            2f);

        Assert.AreEqual(float3.zero, velocity);
    }

    [Test]
    public void ApplyGravityChangesVelocityYWhileAirborne()
    {
        var velocityY = PhysicsMath.ApplyGravity(0f, -10f, 0.5f, 0);

        Assert.AreEqual(-5f, velocityY);
    }

    [Test]
    public void ApplyGravityKeepsGroundedEntityFromAccumulatingDownwardVelocity()
    {
        var velocityY = PhysicsMath.ApplyGravity(0f, -10f, 0.5f, 1);

        Assert.AreEqual(0f, velocityY);
    }

    [Test]
    public void SnapToGroundClampsYAndClearsFallingVelocity()
    {
        var result = PhysicsMath.SnapToGround(
            new float3(2f, -0.25f, 3f),
            new float3(1f, -4f, 5f),
            0f);

        Assert.AreEqual(new float3(2f, 0f, 3f), result.Position);
        Assert.AreEqual(new float3(1f, 0f, 5f), result.Velocity);
        Assert.AreEqual(1, result.IsGrounded);
    }

    [Test]
    public void SnapToGroundKeepsAirbornePositionAndVelocity()
    {
        var result = PhysicsMath.SnapToGround(
            new float3(2f, 5f, 3f),
            new float3(1f, -4f, 5f),
            0f);

        Assert.AreEqual(new float3(2f, 5f, 3f), result.Position);
        Assert.AreEqual(new float3(1f, -4f, 5f), result.Velocity);
        Assert.AreEqual(0, result.IsGrounded);
    }

    [Test]
    public void SnapToGroundFromSensorPlacesSensorBottomOnGround()
    {
        var result = PhysicsMath.SnapToGroundFromSensor(
            new float3(0f, 0.62f, 0f),
            new float3(1f, -3f, 2f),
            0.12f,
            0.1f,
            0f,
            0.03f);

        Assert.AreEqual(new float3(0f, 0.6f, 0f), result.Position);
        Assert.AreEqual(new float3(1f, 0f, 2f), result.Velocity);
        Assert.AreEqual(1, result.IsGrounded);
    }

    [Test]
    public void SnapToGroundFromSensorKeepsAirborneEntityAboveSensorSkin()
    {
        var result = PhysicsMath.SnapToGroundFromSensor(
            new float3(0f, 0.7f, 0f),
            new float3(1f, -3f, 2f),
            0.2f,
            0.1f,
            0f,
            0.03f);

        Assert.AreEqual(new float3(0f, 0.7f, 0f), result.Position);
        Assert.AreEqual(new float3(1f, -3f, 2f), result.Velocity);
        Assert.AreEqual(0, result.IsGrounded);
    }

    [Test]
    public void CalculateGroundSensorWorldShapeUsesEntityTransform()
    {
        var sensor = new GroundSensor
        {
            LocalCenter = new float3(0f, -0.5f, 0f),
            Radius = 0.1f,
            Skin = 0.03f
        };

        var shape = GroundSensorMath.CalculateWorldShape(
            sensor,
            new float3(1f, 4f, 2f),
            quaternion.identity,
            2f);

        Assert.AreEqual(new float3(1f, 3f, 2f), shape.Center);
        Assert.AreEqual(0.2f, shape.Radius);
        Assert.AreEqual(0.23f, shape.QueryRadius);
    }

    [Test]
    public void CreateGroundSensorDataKeepsAuthoringCenterAndClampsNegativeRadius()
    {
        var sensor = GroundSensorAuthoringMath.CreateSensorData(
            new float3(0f, -0.5f, 0f),
            -0.25f,
            -0.03f);

        Assert.AreEqual(new float3(0f, -0.5f, 0f), sensor.LocalCenter);
        Assert.AreEqual(0.25f, sensor.Radius);
        Assert.AreEqual(0.03f, sensor.Skin);
    }

    [Test]
    public void CalculateFacingDirectionUsesVelocityXZAndIgnoresVelocityY()
    {
        var facing = MovementMath.CalculateFacingDirection(
            new float2(0f, 1f),
            new float3(3f, -99f, 4f));

        Assert.AreEqual(new float2(0.6f, 0.8f), facing);
    }

    [Test]
    public void CalculateFacingDirectionKeepsCurrentFacingWhenNotMovingOnXZ()
    {
        var facing = MovementMath.CalculateFacingDirection(
            new float2(0f, -1f),
            new float3(0f, -99f, 0f));

        Assert.AreEqual(new float2(0f, -1f), facing);
    }

    [Test]
    public void CalculateFacingRotationFacesWorldDirection()
    {
        var rotation = MovementMath.CalculateFacingRotation(new float2(1f, 0f));
        var forward = math.forward(rotation);

        Assert.Less(math.distance(new float3(1f, 0f, 0f), forward), 0.0001f);
    }

    [Test]
    public void ResolveHorizontalPenetrationPushesOutOnXZAndKeepsY()
    {
        var resolvedPosition = StaticObstacleCollisionMath.ResolveHorizontalPenetration(
            new float3(0.4f, 2f, 0f),
            new float3(0.4f, 2.5f, 0f),
            0.5f,
            new float3(0f, 2.5f, 0f),
            new float3(1f, 0f, 0f));

        Assert.AreEqual(new float3(0.5f, 2f, 0f), resolvedPosition);
    }

    [Test]
    public void ResolveHorizontalPenetrationFallsBackToSurfaceNormal()
    {
        var resolvedPosition = StaticObstacleCollisionMath.ResolveHorizontalPenetration(
            new float3(0f, 2f, 0f),
            new float3(0f, 2.5f, 0f),
            0.5f,
            new float3(0f, 2.5f, 0f),
            new float3(0f, 0f, 1f));

        Assert.AreEqual(new float3(0f, 2f, 0.5f), resolvedPosition);
    }
}
