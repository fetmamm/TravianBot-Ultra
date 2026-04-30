using System;
using static Tensorflow.Binding;

class Program
{
    static void Main(string[] args)
    {
        tf.enable_eager_execution();

        var a = tf.constant(10);
        var b = tf.constant(5);

        var result = a + b;

        Console.WriteLine($"Result: {result.numpy()}");
    }
}