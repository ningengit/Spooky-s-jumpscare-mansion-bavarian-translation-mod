namespace PlainAndEasyInjector;

// Delegate types with 'ref' parameters, used by ModifyArgs.
public delegate void RefAction<T, A1>(T self, ref A1 a1);
public delegate void RefAction<T, A1, A2>(T self, ref A1 a1, ref A2 a2);
public delegate void RefAction<T, A1, A2, A3>(T self, ref A1 a1, ref A2 a2, ref A3 a3);
public delegate void RefAction<T, A1, A2, A3, A4>(T self, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4);
