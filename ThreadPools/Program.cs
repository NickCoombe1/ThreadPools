using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

/*Async / Await in C#:
Async = Co-currency:
I can do two things at the sametime.
    Need some way to enable this.
    This is what Threadpool does. It sits at the bottom of the stack.
    Threads run on a single CPU/Core usually*/


//STEP 1: will always print 100 as i is still incrementing even when the thread is asleep.
/*for (int i = 0; i < 100; i++)
{
    MyThreadPool.QueueUserWorkItem(() => {
        Console.WriteLine(i);
        Thread.Sleep(1000);
    });
}
Console.ReadLine();*/

/*
//STEP 2: WE can fix this by implemnenting a local value to hold this state

for (int i = 0; i < 100; i++)
{
    int value = i;
    MyThreadPool.QueueUserWorkItem(() => {
        Console.WriteLine(value);
        Thread.Sleep(1000);
    });
}
Console.ReadLine();
*/

//STEP 3 Using AsyncLocal<int> can get around this too. As the .NET ThreadPool passes it around the threads and looks at the ExeuctionContext to keep state consistent.
/*AsyncLocal<int> localValue = new AsyncLocal<int>();

for (int i = 0; i < 100; i++)
{
    // int value = i;
    localValue.Value = i;
    MyThreadPool.QueueUserWorkItem(() => {
        //Console.WriteLine(i);
        //Console.WriteLine(value);
        Console.WriteLine(localValue.Value);
        Thread.Sleep(1000);
    });
}
Console.ReadLine();*/

//STEP 4: Implement task

/*
AsyncLocal<int> localValue = new AsyncLocal<int>();
List<MyTask> myTasks = new();

for (int i = 0; i < 100; i++)
{
    // int value = i;
    localValue.Value = i;
    myTasks.Add(MyTask.Run(delegate
    {
        //Console.WriteLine(i);
        //Console.WriteLine(value);
        Console.WriteLine(localValue.Value);
        Thread.Sleep(1000);
    }));
}

MyTask.WhenAll(myTasks).Wait();*/

//Step 5: Async await
MyTask.Iterate(PrintAsync()).Wait();
static IEnumerable<MyTask> PrintAsync()
{
    for (int i = 0; ; i++)
    {
        //now effectively have Async/ await
        yield return MyTask.Delay(1000);
        Console.WriteLine(i);
    }
}



class MyTask
{
    private bool _completed;
    private Exception? _exception;
    private Action? _continuation;
    private ExecutionContext? _context;

    public bool IsCompleted
    {
        get
        {
            lock (this)
            {
                return _completed;
            }
        }
    }

    public void SetResult() => Complete(null);

    public void SetException(Exception ex) => Complete(ex);

    private void Complete(Exception? ex)
    {
        lock (this)
        {
            if (_completed) throw new InvalidOperationException();

            _completed = true;
            _exception = ex;
            if (_continuation is not null)
            {
                MyThreadPool.QueueUserWorkItem(delegate
                {
                    if (_context is null)
                    {
                        _continuation();
                    }
                    else
                    {
                        ExecutionContext.Run(_context, (object? state) => ((Action)state!).Invoke(), _continuation);
                    }
                });
            }
        }
    }

    public void Wait()
    {
        // a manual reset event 
        ManualResetEventSlim? mres = null;

        lock (this)
        {
            // if our task isnt finished, lets create a ResetEvent. These reset events block current threads while waiting on another 
            if (!_completed)
            {
                mres = new ManualResetEventSlim();
                ContinueWith(mres.Set);
            }
        }

        mres?.Wait();

        if (_exception is not null)
        {
            ExceptionDispatchInfo.Throw(_exception);
        }
    }

    public MyTask ContinueWith(Action action)
    {
        MyTask t = new();

        Action callback = () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }

            t.SetResult();
        };

        lock (this)
        {
            //if our task is done, lets call the continuation function
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                //if not lets store it for now
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }

        return t;
    }

    //allows us to wait for any child tasks to finish 
    public MyTask ContinueWith(Func<MyTask> action)
    {
        MyTask t = new();

        Action callback = () =>
        {
            try
            {
                MyTask next = action();
                next.ContinueWith(delegate
                {
                    if (next._exception is not null)
                    {
                        t.SetException(next._exception);
                    }
                    else
                    {
                        t.SetResult();
                    }
                });
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }
        };

        lock (this)
        {
            //if our task is done, lets call the continuation function
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                //if not lets store it for now
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }

        return t;
    }

    public static MyTask Run(Action action)
    {
        MyTask t = new();
        MyThreadPool.QueueUserWorkItem(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }

            t.SetResult();
        });
        return t;
    }

    public static MyTask WhenAll(List<MyTask> myTasks)
    {
        //create a task to manage this 
        MyTask t = new();
        if (myTasks.Count == 0)
        {
            t.SetResult();
        }
        else
        {
            int remain = myTasks.Count;
            Action continuation = () =>
            {
                // use interlocked to sync the remain value
                if (Interlocked.Decrement(ref remain) == 0)
                {
                    t.SetResult();
                }
            };
            foreach (MyTask myTask in myTasks)
            {
                myTask.ContinueWith(continuation);
            }
        }

        return t;
    }

    public static MyTask Delay(int timeout)
    {
        MyTask t = new();
        new Timer(_ => t.SetResult()).Change(timeout, -1);
        return t;
    }

    public static MyTask Iterate(IEnumerable<MyTask> myTasks)
    {
        MyTask t = new();
        IEnumerator<MyTask> enumerator = myTasks.GetEnumerator();

        //this will allow us to use yield
        void MoveNext()
        {
            while (enumerator.MoveNext())
            {
                MyTask next = enumerator.Current;
                next.ContinueWith(MoveNext);
                return;
            }
            t.SetResult();
        }
        MoveNext();
        return t;
    }
}

static class MyThreadPool
{
    private static readonly BlockingCollection<(Action, ExecutionContext?)> WorkItems =
        new BlockingCollection<(Action, ExecutionContext?)>();

    //We use action which is a parameterless delegate that's already been defined by .NET
    //Execution context is basically a dictionary of key value pairs of the context when we queued a work item
    public static void QueueUserWorkItem(Action action) => WorkItems.Add((action, ExecutionContext.Capture()));

    static MyThreadPool()
    {
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            //for each core, lets start a new thread
            new Thread((() =>
                        {
                            while (true)
                            {
                                // Take a work item and run it
                                (Action workItem, ExecutionContext? context) = WorkItems.Take();
                                if (context is null)
                                {
                                    workItem();
                                }
                                else
                                {
                                    //ON run, workItem gets stuffed into state and run
                                    ExecutionContext.Run(context, (object? state) => ((Action)state!).Invoke(),
                                        workItem);
                                }
                            }
                        }
                        //Background threads mean we don't want this threads' process to wait for any other created threads to finish.
                    ))
                { IsBackground = true }.Start();
        }
    }
}
//you shouldn't generally lock but because its low level code its okay for this  