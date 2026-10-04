using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// A small Breakout game drawn by one Avalonia control. The host steps it once per game frame and
/// feeds it pointer positions in control coordinates.
/// </summary>
internal sealed class BreakoutGame : Control
{
    private const int Columns = 8;
    private const int Rows = 5;
    private const double Gutter = 10;
    private const double BrickGap = 4;
    private const double BrickHeight = 12;
    private const double BricksTop = 34;
    private const double PaddleWidth = 64;
    private const double PaddleHeight = 8;
    private const double BallSize = 7;
    private const double BallSpeed = 230;
    private const double MaxStep = 3;
    private const int StartingLives = 3;

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x19, 0x27, 0x36));
    private static readonly IBrush Ink = Brushes.White;
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x9f, 0xb4, 0xc7));
    private static readonly IBrush PaddleBrush = new SolidColorBrush(Color.FromRgb(0xe8, 0xc5, 0x6a));
    private static readonly IBrush[] RowBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0xc8, 0x4b, 0x3c)),
        new SolidColorBrush(Color.FromRgb(0xd9, 0x84, 0x3a)),
        new SolidColorBrush(Color.FromRgb(0xd8, 0xbd, 0x4a)),
        new SolidColorBrush(Color.FromRgb(0x5d, 0xa8, 0x5a)),
        new SolidColorBrush(Color.FromRgb(0x32, 0x75, 0x8d))
    };
    private static readonly Typeface Font = new(FontFamily.Default);

    private readonly List<Rect> _bricks = new();
    private double _paddleX;
    private Point _ball;
    private Vector _velocity;
    private bool _launched;
    private int _score;
    private int _lives;
    private string? _banner;

    public BreakoutGame(double width, double height)
    {
        Width = width;
        Height = height;
        _paddleX = (width - PaddleWidth) / 2;
        NewGame();
    }

    /// <summary>Move the paddle's centre to a pointer x position.</summary>
    public void PointAt(double x)
    {
        _paddleX = Math.Max(0, Math.Min(Width - PaddleWidth, x - (PaddleWidth / 2)));
        if (!_launched)
            ParkBall();
        InvalidateVisual();
    }

    /// <summary>Launch the ball, or start a new game after a win or loss.</summary>
    public void Click()
    {
        if (_lives == 0 || _bricks.Count == 0)
        {
            NewGame();
            return;
        }

        if (_launched)
            return;
        _launched = true;
        _banner = null;
        _velocity = new Vector(BallSpeed * 0.45, -BallSpeed * 0.89);
    }

    /// <summary>Advance the ball by real elapsed time, in small steps so it can't pass through a brick.</summary>
    public void Step(TimeSpan elapsed)
    {
        if (!_launched)
            return;

        var seconds = Math.Min(elapsed.TotalSeconds, 0.05);
        var steps = Math.Max(1, (int)Math.Ceiling(BallSpeed * seconds / MaxStep));
        for (var i = 0; i < steps && _launched; i++)
            Move(seconds / steps);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(0, 0, Width, Height));

        for (var i = 0; i < _bricks.Count; i++)
            context.FillRectangle(RowBrushes[RowOf(_bricks[i])], _bricks[i], 2);

        context.FillRectangle(PaddleBrush, PaddleRect(), 3);
        context.DrawEllipse(Ink, null, new Rect(_ball.X, _ball.Y, BallSize, BallSize));

        DrawText(context, $"Score {_score}", 13, Ink, new Point(Gutter, 8));
        var lives = Text($"Lives {_lives}", 13, Ink);
        context.DrawText(lives, new Point(Width - Gutter - lives.Width, 8));

        if (_banner != null)
        {
            var banner = Text(_banner, 14, Muted);
            context.DrawText(banner, new Point((Width - banner.Width) / 2, Height * 0.62));
        }
    }

    private void NewGame()
    {
        _bricks.Clear();
        var brickWidth = (Width - (2 * Gutter) - ((Columns - 1) * BrickGap)) / Columns;
        for (var row = 0; row < Rows; row++)
            for (var column = 0; column < Columns; column++)
                _bricks.Add(new Rect(
                    Gutter + (column * (brickWidth + BrickGap)),
                    BricksTop + (row * (BrickHeight + BrickGap)),
                    brickWidth,
                    BrickHeight));

        _score = 0;
        _lives = StartingLives;
        _launched = false;
        _banner = "Click to launch";
        ParkBall();
        InvalidateVisual();
    }

    private void Move(double seconds)
    {
        _ball += _velocity * seconds;

        if (_ball.X <= 0 || _ball.X + BallSize >= Width)
        {
            _ball = _ball.WithX(Math.Max(0, Math.Min(Width - BallSize, _ball.X)));
            _velocity = _velocity.WithX(-_velocity.X);
        }
        if (_ball.Y <= 0)
        {
            _ball = _ball.WithY(0);
            _velocity = _velocity.WithY(Math.Abs(_velocity.Y));
        }

        var ball = BallRect();
        var paddle = PaddleRect();
        if (_velocity.Y > 0 && ball.Intersects(paddle))
        {
            // Bounce off the paddle at an angle set by where the ball hit it.
            var offset = Math.Max(-1, Math.Min(1, (ball.Center.X - paddle.Center.X) / (PaddleWidth / 2)));
            var angle = offset * 1.05;
            _velocity = new Vector(Math.Sin(angle), -Math.Cos(angle)) * BallSpeed;
            _ball = _ball.WithY(paddle.Top - BallSize);
            return;
        }

        for (var i = 0; i < _bricks.Count; i++)
        {
            var brick = _bricks[i];
            if (!ball.Intersects(brick))
                continue;

            var overlap = ball.Intersect(brick);
            _velocity = overlap.Width < overlap.Height ? _velocity.WithX(-_velocity.X) : _velocity.WithY(-_velocity.Y);
            _score += 10 * (Rows - RowOf(brick));
            _bricks.RemoveAt(i);
            if (_bricks.Count == 0)
            {
                _launched = false;
                _banner = "You win! Click to play again";
            }
            return;
        }

        if (_ball.Y > Height)
        {
            _lives--;
            _launched = false;
            _banner = _lives == 0 ? "Game over. Click to play again" : "Click to launch";
            ParkBall();
        }
    }

    private void ParkBall() => _ball = new Point(_paddleX + ((PaddleWidth - BallSize) / 2), PaddleTop() - BallSize - 1);

    private int RowOf(Rect brick) => (int)Math.Round((brick.Y - BricksTop) / (BrickHeight + BrickGap));

    private double PaddleTop() => Height - 22;

    private Rect PaddleRect() => new(_paddleX, PaddleTop(), PaddleWidth, PaddleHeight);

    private Rect BallRect() => new(_ball.X, _ball.Y, BallSize, BallSize);

    private static void DrawText(DrawingContext context, string text, double size, IBrush brush, Point origin) =>
        context.DrawText(Text(text, size, brush), origin);

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
}
